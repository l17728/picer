# 系统审查问题修复记录

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
> 后续替代：本文初审代码缺口已由后续修复关闭；目录跟踪现支持持久 NTFS USN 连续水位，视频内容及派生证据按内容共享，移动修复和手动清除覆盖视频，本地照片/视频均有界并发。尚需真实设备/模型验证的边界仍保留，不能把初审建议重新列为当前代码待办。
<!-- /historical-status-2026-10-10 -->

后续状态：本文所述的进程内扫描、完整分页镜像、视频派生复制和缺少视频清除入口，已由[剩余代码任务实现](REMAINING_CODE_IMPLEMENTATION_2026-10-09.md)继续补齐；本文保留前一轮实现与测试证据。

本文记录用户在系统审查后要求“修复”的实施结果。原四项 P2（人脸模型质量、人工 ASR 对照、真人 AEC3、低显存方案）保持暂停。此前审查报告保留发现时的证据，本报告及[当前能力矩阵](CURRENT_CAPABILITIES_2026-10-09.md)标明后续状态。

## 修复内容

| 审查项 | 实现及回归依据 |
|---|---|
| R03 人脸重检 | 同模型空间重叠、双向唯一匹配保留稳定 ID，保留人工归属/忽略/拒绝；歧义或消失的人工证据归档，不参与实时候选。整体事务失败回滚；镜像和内容副本携带归档属性。FaceDetectionReconciliationTests。 |
| R04 迁移安全 | schema43。关闭所有写入者后整文件备份 DB/WAL/SHM，在暂存副本完成迁移、完整性与外键检查；原子替换和持久恢复标记保护中断发布。拒绝活动连接升级和未来版本；原始 WAL 校验提交记录。DatabaseMigrationSafetyTests、VideoSchemaMigrationTests、AnalysisBillingMigrationTests。 |
| PERF03 镜像一致性 | 移除按照片数量相同跳过更新的捷径；每次按页同步。同数量的路径、内容签名、缺失状态变化仍应用。MirrorImageRefreshTests。当前选择完整分页校准，尚无服务端增量水位。 |
| PERF02 扫描导入 | 页内批量按路径查询；首次扫描建立轻量缺失快照，不再每页物化整个来源。source/path NOCASE 索引支持查询，重复或缺失页保守处理。DirectoryScanPagingTests。 |
| F01–04 检索 | 本名与别名统一最长提及解析；明确人物采用全体交集，模型不能绕过词边界重新加人；复核 null/缺字段安全回退；照片视频均支持 GPS 与地点别名召回。AiMediaReviewRegressionTests。 |
| 解释与证据一致性 | 程序确认结果单独生成数量、人物交集和检索条件；流式 AI 解释放入明确标记的引用区，不改写确定性结果。没有有效候选时丢弃模型声称的命中并跳过解释请求。AiMediaReviewRegressionTests。 |
| R05/R06、PERF08 任务存储 | 最新状态采用独立 SQLite 索引；旧 JSONL 流式导入成功后截断。响应及回放流式传输，落盘故障不改变已成功业务响应；重试返回前等待持久结果完成。ServiceTaskStorageTests、ServiceRetryRecoveryTests。 |
| PERF06 缩略图 | 每个调用者独立取消等待；共享下载使用缓存自己的期限令牌，按任务身份清理；UI 工厂接收 downloadToken。RemoteCacheConcurrencyTests。 |
| PERF07 资源缓存 | 按内容互斥、最多4路下载、可取消 SHA-256、容量限制和 LRU 索引；清理后旧下载不能回填。RemoteCacheConcurrencyTests。 |
| PERF04/05 目录响应 | SQL 子树范围和批量有效描述状态查询，路径表达式索引；SQL 去重父目录、后台树模型、逐层创建控件、取消过期预览。IncrementalDescriptionSearchTests、DirectoryMediaDescriptionTests。 |
| 共享数据库连接 | 人物详情、人脸详情、Florence 读写纳入 UiWork；问答及文字索引重建使用独立连接，避免长期模型请求占住公共队列。位置反查已有独立连接；保留图像解码等无数据库工作的 Task.Run。 |
| 请求费用 | 不覆盖的匿名请求账目，迁移时导入仍存在的历史费用一次；照片/视频请求预留预算，成功按实际返回用量记账，同 requestId 幂等，失败/取消释放预留。AnalysisBillingTests、AnalysisBillingMigrationTests。 |
| 易用性 | 目录照片/视频失败清单含阶段、错误码、可重试性，取消不记失败；仅重试可重试项。明确扫描前索引预览与扫描后实际范围；远程任务照片通过媒体网关打开；统一“照片分析”标题/完成/取消/失败文案。 |

## 费用与保留边界

- 预算采用应用估算，默认每个云端请求预留 ¥0.02，成功后记实际返回的估算费用；它不是服务商账单或硬性扣费上限。服务商未返回用量、网络中断后服务商是否扣费，不能仅凭客户端确定。此前已被旧版本覆盖的费用无法恢复。
- 本地模型不预留云端费用。预算约束以当前数据库为单位，不是多个独立客户端共享的账户级配额。匿名费用账目不保存媒体 ID、路径、哈希、人脸信息或文字索引，不阻碍手动清除媒体索引。
- 服务结果默认单项64 MiB、合计512 MiB、最多2000项、7天过期、4路执行；任务最新状态保留30天、最多10万项。结果过期或写入失败可保留业务成功状态，回放明确不可用。
- 当前可回放任务只含视频 probe/audio/keyframes/transcribe/people；当前手动彻底清除接口针对照片，二者没有照片结果关联。未来若增加视频彻底删除，必须同时清理对应任务结果，不能复用照片 ID 推断视频关联。
- 归档人脸仍属原照片的人工证据，手动彻底清除照片时沿用原来的关联清理，不作为新候选重新出现。

## 三项照片描述失败

先前真实任务记录中三项均为 `SqliteConnection does not support nested transactions`，最近发生于北京时间 2026-10-09 10:39:40。原因是共用 SQLite 连接的描述写入与镜像事务竞争；普通入口、目录入口和后续保存已统一连接所有权。该证据不是 HTTP 502 的证据，历史 SSL/503 也不与它合并判断。

未伪造任务成功、删除历史失败或重新请求真实模型。新增逐项错误码保留 HTTP 状态和 SQLite 错误码，支持区分预处理、模型、保存问题；不会把凭据或完整模型文本写入错误日志。

## 验证与发布

- `dotnet restore picer.sln` 成功；严格 Debug 构建零警告、零错误。
- 修复后定向单元 **46/46** 通过：`artifacts/system-review-tests/review-fixes-final-targeted.trx`。
- 完整非 RuntimeComponent WPF **70/70** 通过：`review-fixes-final-wpf.trx`；最后取消状态兼容补丁后的目录 WPF **6/6** 通过：`review-fixes-final-directory-wpf.trx`。
- 非 RuntimeComponent 单元用例共 **520 项均完成通过验证**：最后一次完整运行 **519/520** 通过，唯一失败为扫描测试环境竞态（`review-fixes-final-units.trx`）；随后只修改该测试的环境隔离和通知稳定等待，产品代码未变。扫描相关定向 **18/18** 通过（`review-fixes-scan-final.trx`），该用例再作3次独立复测均通过（`review-fixes-scan-repeat-1/2/3.trx`）。不把分次复测描述为一次520/520的完整运行；合并核验清单为 `review-fixes-verification.json`，没有未解决的失败用例。
- 10万条合成索引基准：本地列表/搜索 p95 为 **83.3/94.8 ms**，HTTPS列表/搜索为 **74.0/88.8 ms**，每组10次有效采样。仅代表隔离索引分页和检索，不代表真实模型吞吐或原图库磁盘速度。
- 最终 `git -c core.whitespace=cr-at-eol diff --check` 通过。
- 首轮失败输出保留：decimal 参数的 SQLite 比较类型、Windows 排他句柄下的原子替换、历史 schema fixture、目录模式转义、内存库路径、过时数据库调用次数断言，以及监控测试环境竞态。未降低产品数据保护规则或隐藏历史失败。

此轮未部署线上服务、未迁移真实图库，原发布目录不包含这些修改。真实部署必须共同升级客户端、源代理及索引服务，并先关闭所有写入者；源码和测试通过不等于真实环境已更新。
