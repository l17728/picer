# 系统审查：健壮性、数据一致性与失败恢复（2026-10-09）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
> 后续替代：本文初审代码缺口已由后续修复关闭；目录跟踪现支持持久 NTFS USN 连续水位，视频内容及派生证据按内容共享，移动修复和手动清除覆盖视频，本地照片/视频均有界并发。尚需真实设备/模型验证的边界仍保留，不能把初审建议重新列为当前代码待办。
<!-- /historical-status-2026-10-10 -->

## 范围与结论口径

本报告为本轮多领域系统审查的健壮性分报告，覆盖数据库升级、照片描述写入、索引同步、人工人脸状态、服务任务持久化与共享连接访问。此前模型质量、ASR、真人 AEC3、低显存验收等 P2 工作暂停，本报告的严重性标签是新发现的审查优先级，不表示恢复执行旧 P2。

证据来自当前工作区代码及实际数据库的只读汇总查询。未修改真实索引、未调用付费模型、未输出照片路径、人名、向量、提示词或凭据。代码行号以本轮审查时工作区为准；并行编辑后以符号名辅助定位。没有覆盖到的路径不能据此认为无缺陷。

| 编号 | 严重性 | 状态 | 结论 |
|---|---|---|---|
| R-01 | P1 | 已修改，待统一测试 | 普通照片描述工厂未加入共享数据库门锁 |
| R-02 | P1 | 已修改，待统一测试 | 照片同步分批循环实际重复发送整个来源全集 |
| R-03 | P1 | 已实现，待统一最终验证 | 服务端重复人脸检测删除人工关联及审核状态 |
| R-04 | P1 | 已实现，待统一最终验证 | 普通旧库启动升级缺少自动关闭文件备份及完整原子性 |
| R-05 | P2 | 已实现并验证 | 流式响应和回放、TTL/容量/数量限额、落盘失败不影响实时业务成功；见后续修复记录 |
| R-06 | P2 | 已实现并验证 | SQLite 最新状态索引、启动仅处理非终态、旧日志导入后截断 |
| R-07 | P2 | 已实现并验证 | 任务中心及人物/人脸/Florence窗口使用公共执行器，问答与文本重建使用独立连接 |

## R-01：照片描述三条失败调查及补锁

### 实际证据与时间边界

只读查询实际 `remote-mirror.db` 的 `cloud_vision_analyses`，恰有 3 条失败错误为：

```text
InvalidOperationException: SqliteConnection does not support nested transactions.
```

最近失败时间为 `2026-10-09T02:39:40.0928976Z`，即北京时间 10:39:40。该时间早于前轮最终新版 GUI 于 11:56 左右启动。因此它们是保留的历史失败，不能直接声称最新版本再次复现了三条失败。同表另有 28 条 SSL 连接失败、2 条 HTTP 503，发生更早，未与这三条混为同一根因。

错误直接证明共享 SQLite 连接遇到重叠事务；数据库仅保存错误摘要，没有完整异常栈，不能断言历史每次竞争对应的具体后台调用栈。

### 当前代码触发链与修复

- `src/Picer.App/ViewModels/MainViewModel.cs:1353`，`AnalyzeImagesWithCloudAsync` 使用照片分析工厂。
- `src/Picer.App/AppServices.cs:589`，`CreateCloudVisionService` 原先未向 `CloudVisionAnalysisService` 注入 `databaseWork`。
- `src/Picer.Core/Vision/CloudVisionAnalysisService.cs:20` 的默认 `_singleWorker` 属于服务实例，无法协调另一个分析实例或远程镜像的数据库操作。
- `src/Picer.Core/Search/MediaTextIndexRepository.cs:25`，描述保存中的 FTS 写入开启事务；后台镜像也在同一 AppServices.Db 连接上写事务。
- `src/Picer.App/Infrastructure/UiWork.cs:11` 的共享 DatabaseGate 已用于镜像及新目录描述入口，但普通工厂仍绕开它。

本轮将普通照片工厂也接入 `UiWork.RunDatabaseAsync`。数据库门锁只保护短数据库工作，不包住模型请求，因此不使网络推理串行化。

新增 `tests/Picer.Tests/CloudVisionTests.cs:18` 的 `SharedDatabaseExecutorWaitsForMirrorTransactionBeforeSavingDescriptions`：两个独立服务完成模型结果时，外部镜像事务持有共享门锁；两次保存均等待，镜像事务提交后完成，断言两次成功及零失败。

首次定向构建被另一并行编辑尚未完成的 `IndexRepairService.ApplyVideo` 引用阻断，并非测试断言失败。最终构建及执行由主代理统一完成，本分报告编写时不宣称测试已通过。历史失败行未篡改为成功，需正常重试成功后才改变状态。

## R-02：照片同步重复发送全集

**证据**：`src/Picer.Core/Gateway/IndexSyncClient.cs:89-91`，`PushImagesAsync` 遍历 `images.Chunk(200)`，原请求匿名对象却发送 `images` 全集，而不是当前 `batch`。

**触发与影响**：某来源超过 200 张照片时，每一批均上传全部照片。N 张照片重复发送约 `ceil(N/200)` 次；8603 张约 44 份全量载荷。进度只累加 batch.Length，看起来正常，实际网络、反序列化和数据库工作显著放大，并增加超时及请求体限制风险。

**本轮修复**：请求改为 `new { sourcePath = source.Path, images = batch }`，保持现有协议及幂等写入语义。

**回归**：`tests/Picer.Tests/Gateway/IndexSyncBatchTests.cs:12`，使用隔离临时数据库和 HTTP handler，401 张生成严格的 200/200/1 三个请求；断言每批不超过 200、每个 ID 恰好发送一次、返回计数 401。未访问真实服务。由主代理统一构建执行。

## R-03：重复人脸检测删除人工状态

**证据与触发链**：

- `src/Picer.Core/Vision/ServiceFaceAnalyzer.cs:45`，实际请求含 imageId。
- `server/Picer.IndexService/IndexServiceHost.cs:971-979`，`/api/service/v1/face/{operation}` 对 detect/embed 先 DELETE 同图同模型的 detected_faces，再逐条 INSERT；该替换块没有整体事务。
- `src/Picer.Core/Data/PicerDatabase.cs:229`、`:238`、`:561`，人工关联、拒绝及审核状态通过 face_id 外键删除级联。

再次检测一个已审核的照片时，旧 faceId 被删除，人工状态随之删除；新插入的人脸拥有新 ID。若插入途中失败，还可能留下部分检测结果。当前视频人脸路由已采用保留 ID 的更新策略，并不能保护此照片路由。

**建议**：明确同模型重新检测的稳定身份匹配规则；在完整事务中更新已有 faceId、增加确实新增项，并针对消失项保留可追溯人工决策。不能盲目把同序号视作同一张脸而误转人工身份。

**建议测试**：重复相同检测保留 confirmed/ignored/rejected；顺序变化与人脸数量增减不误配；故障注入到中间插入时旧结果及人工状态原子保留。

## R-04：旧库自动升级缺少可恢复边界

**证据**：`src/Picer.Core/Data/PicerDatabase.cs:30-49` 在构造时打开数据库并开始 schema 初始化，仅新数据库进入 initialSchemaTransaction；`:815-816` 的连续 ALTER、`:834` 的 ALTER 没有统一旧库迁移事务。`src/Picer.App/AppServices.cs:84/:134`、`server/Picer.IndexService/IndexServiceHost.cs:326` 直接构造数据库，未先完成 AGENTS.md 要求的关闭文件整库备份。

部署脚本提供备份不能覆盖所有用户直接替换 exe 后启动旧库的路径。旧库升级期间异常或掉电会留下已提交的部分 DDL；某些裸 ADD COLUMN 重跑可能因列已存在失败。版本号写入与所有该版本变更也需要一体提交。

**建议**：在打开读写库之前探测 schema，要求关闭并备份 DB/WAL/SHM；逐版本迁移必须原子提交，失败保持旧版本，拒绝以旧程序写入未来 schema。对已半迁移数据给出明确恢复路径，而非直接抑制异常。

**建议测试**：每个迁移阶段注入异常、强制中止后重启；验证旧库数据、版本号、备份完整性及重复升级结果。此项仅代码审查，未对真实旧库做破坏性故障实验。

## R-05：完整服务任务响应无保留或容量策略

**证据**：`server/Picer.IndexService/ServiceTaskRunner.cs:35` 的可重放范围包括 video-audio、video-people；`:58-65` 将完整响应放入 MemoryStream，ToArray 后序列化 byte[] 为 JSON 保存。`server/Picer.IndexService/IndexServiceHost.cs:800-811` 的音频响应允许达到 32 MiB。每次新任务生成新 GUID 结果文件，当前 runner 没有过期清理、容量上限或媒体清理联动。

**影响**：反复提取音频或检测人脸会保留大量二进制及人脸结果。并发时存在内存缓冲、数组副本与 JSON 字符串的额外峰值；长期运行造成磁盘及备份增长。落盘异常在业务已经完成后仍可能让请求失败。这些是代码确定的资源生命周期缺口，尚未执行磁盘耗尽压力试验。

**建议**：大结果以受限流落盘，日志仅保存引用；明确结果保留期限、容量配额、文件权限及用户彻底删除后的清理边界；记录成功业务状态和结果交付状态的区别。

**建议测试**：并发大音频响应内存上限、磁盘满、写一半重启、结果过期、清理与读取竞争，以及关联媒体清理后任务结果是否仍可访问。

## R-06：任务日志查询与恢复全量扫描

**证据**：`server/Picer.IndexService/TaskJournal.cs:23` 只追加；`:31` 的 ReadLines.TakeLast 即使只取 200 条仍读取整个文件。`:11-17` 的 MarkInterrupted 使用 Read(int.MaxValue)，物化历史后分组；Retry 也查询全历史。

**影响**：随着总事件数上涨，列表和重试延迟线性增大，重启恢复有全量内存需求。没有观察到当前生产环境因此崩溃，不应把容量隐患写成已发生事故。

**建议**：使用带 requestId 索引的持久状态表、限定保留的事件日志和分页查询；启动仅查非终态任务。增加十万至百万事件下列表、重试和启动恢复的时间与内存基准。

## R-07：任务中心绕过共享数据库执行器

**证据**：`src/Picer.App/CloudVisionTaskCenterWindow.xaml.cs:17` 用 Task.Run 调用共享 CloudVisionRepository.GetRecentTasks；`:25` 用 Task.Run 读取共享 AppServices.Db.GetImage。二者未进入 UiWork.DatabaseGate，也没有独立只读连接。

**判断边界**：这里只能确认同一个 SqliteConnection 可被不同线程同时使用的路径；读取自身不开新事务，不能直接把它认定为上述三条“嵌套事务”失败的来源，当前没有定向复现证明。

**本轮处理**：主审已将任务列表和重试前的记录读取改为 UiWork 共享执行器，并明确列表仅统计最近任务。这里不将该只读入口认定为三条嵌套事务异常的直接来源。其他窗口的共享连接调用仍须逐步统一，见性能专项报告。

## 验证与交付边界

本专项修改了照片分析工厂共享门锁及照片同步 batch 载荷，并新增两个定向回归测试。结构性数据库升级、人脸身份替换和任务持久化调整只记录待办，避免在广泛审查期间未经完整设计大改数据边界。主报告应补入统一严格构建和测试的最终结果；本分报告不替代最终测试记录。

## 后续授权修复：R-03 / R-04（2026-10-09）

用户随后明确授权修复全部审查问题。以下是本专项的实际修复，不表示恢复此前暂停的四项 P2 验收。

### 人脸稳定身份及归档

新增 `src/Picer.Core/Vision/FaceDetectionReconciler.cs`，本地 `VisionAnalysisRepository.SaveCompleted` 和服务端 face detect/embed 端点统一调用事务协调器。同模型中，仅空间框 IoU 至少 0.75 且双向唯一匹配才复用原 faceId/ordinal；检测输出重排不会交换人工身份。重复结果保留 confirmed、ignored、rejected 等原关系，新项使用未占用序号。

未匹配且带人工决策的旧脸保留为归档证据，设置 `is_archived=1`，不把人工身份转给新脸。实时脸框、未分类/候选队列及匹配向量排除归档。以后同模型无歧义地再次找到原空间身份时可解除归档，仍保留原决策。没有人工证据的失效检测可删除。模型版本不一致或无效输入在写入前拒绝；中途数据库异常回滚整个结果写入。

统一 v43 迁移添加归档列；`FaceArchiveMigration` 更新 content capture/restore 触发器，归档状态随内容副本恢复。`GatewayDetectedFace` 增加可选 IsArchived 字段，镜像读写和服务端 metadata 同步该状态。

新增 `FaceDetectionReconciliationTests` 六项，覆盖重排保留三种决策、归档排除实时结果及重新找回、数据库 trigger 故障原子回滚、歧义不转移身份、跨模型拒绝、镜像和内容副本保留归档。

### 关闭备份与暂存迁移

新增 `src/Picer.Core/Data/DatabaseMigrationSafety.cs`。数据库构造以路径命名 mutex 串行启动；连接持有共享 `.access` 生命周期租约，升级必须取得独占租约。活动实例会使升级明确拒绝。连接禁用池化，升级前清理已关闭的池化句柄；原 DB/WAL/SHM 全部保持文件句柄，Windows/Linux 通过整文件范围锁与 SQLite 原生锁互斥，原子替换期间不释放保护。

版本探测直接读取主文件页一和 WAL 有效已提交页一，不打开 SQLite，不创建或恢复 SHM。WAL 校验魔数、格式、头部校验和、逐帧累计校验和；无效尾帧不能提供版本或提交证据。算法依据 [SQLite 官方数据库文件格式](https://www.sqlite.org/fileformat2.html#checksum_algorithm)。不依赖可能滞后的 mtime 缓存。高频 `OpenIndependentConnection` 利用现有数据库生命周期约束，不再遍历 WAL；独立连接仍持有自己的租约并用 SQLite 验证版本，不允许在此入口迁移活库。

关闭状态下备份 DB/WAL/SHM 原始字节并刷盘，迁移只在同目录暂存副本执行。完成 quick_check、foreign_key_check 和 WAL checkpoint 后，将暂存库转换为单主文件。发布前刷盘写入恢复标记，再原子替换；发布过程中断，下次打开前依据标记恢复原始三文件备份并重新升级。成功后实时连接恢复 WAL 模式。未来 schema 在写入前拒绝；普通迁移失败保持原库及备份。

新增 `DatabaseMigrationSafetyTests` 八项：暂存失败原字节保留、成功发布、未完成发布恢复、活连接拒绝、关闭 WAL/SHM 整体备份、损坏 WAL 头拒绝、未来 schema 拒绝、独立连接及生命周期释放。`VideoSchemaMigrationTests` 撤销 v43 实际结构后构造 v41，避免只改版本号制造伪旧库；v34 最小 fixture 补入当时已有的视频摘要和照片费用字段，没有把正式迁移改为忽略缺表。`MirrorImageRefreshTests` 覆盖数量不变时同 ID 的路径、时间、大小及 missing 状态更新。

新增 `LegacySchemaFixture` 统一撤销 v43 费用表、归档列/触发器及四个目录索引；`AnalysisBillingMigrationTests` 验证 v42 的照片和视频费用升级后仅种子化一次，重新启动不增加金额或条目，账本没有媒体关联列或外键。

### 验证边界

本专项未操作真实数据库，未自行构建或部署。主代理定向验证曾反馈数据专项 33 项通过、1 项失败；失败是旧 v34 最小 fixture 缺少 v8 照片费用字段，现已补齐，等待统一重跑。Windows 原子替换保护及新的人脸和迁移回归已在该轮通过。最终结果以主代理后续统一报告为准。

完整新进程启动仍需校验 WAL 有效前缀，超大 WAL 存在顺序读取成本；独立连接已避开此热点路径。

主审收尾：上述两项定向已通过；完整单元464/464，最后相关定向111/111、关键帧并发1/1，完整WPF69/69通过，严格构建零警告零错误。主审还将任务中心共享读取和视频描述读写接入公共执行器，服务端关键帧使用独立连接；新v42迁移有事务，旧版本迁移恢复风险仍保留。具体证据及实际部署边界见 [系统审查总报告](SYSTEM_REVIEW_2026-10-09.md)。
