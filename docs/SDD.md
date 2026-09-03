# Picer 软件设计说明（SDD）

版本：1.2
状态：核心功能完成，数据库 v14；AI 问答检索层进入设计阶段
更新时间：2026-09-01

## 1. 目的与范围

本文定义 Picer 的目标架构、关键数据流、安全边界、性能策略和验收方式。产品行为以 [产品与体验设计](PRODUCT_DESIGN.md) 为准，实施顺序以 [开发计划](DEVELOPMENT_PLAN.md) 为准。

## 2. 系统上下文

Picer 是 Windows 桌面应用，当前采用 .NET 8、WPF、MVVM、SQLite、ImageSharp，并支持本地存储和 WebDAV。

主要组件：

- `Picer.App`：窗口、导航、ViewModel、任务反馈和用户确认。
- `Picer.Core.Indexing`：枚举、增量索引、文件身份、元数据和派生资产调度。
- `Picer.Core.Search`：结构化搜索、FTS、语义标签和重复候选生成。
- `Picer.Core.Data`：SQLite schema、事务、分页查询和迁移。
- `Picer.Core.Storage`：本地/WebDAV文件读取和缓存。
- `Picer.Core.Thumbnails`：缩略图生成、版本化缓存和内存缓存。
- 新增 `Picer.Core.FileOperations`：经过授权的回收站操作及结果核验。

## 3. 架构原则

### 3.1 读取与修改边界

索引、搜索、问答、标签和重复检测只允许读取原文件。所有文件修改能力必须通过独立的 `IUserAuthorizedFileOperation` 边界，Core 的索引服务不得删除原文件。

建议接口：

```csharp
public interface IUserAuthorizedFileOperation
{
    Task<FileOperationResult> MoveToRecycleBinAsync(
        IReadOnlyList<AuthorizedFileSelection> selections,
        CancellationToken cancellationToken);
}
```

`AuthorizedFileSelection` 必须包含稳定文件身份、用户确认时路径、大小和修改时间。执行前重新读取文件信息；若内容或路径发生变化，该项失败并返回冲突，不可盲目执行。

现有 `IndexEngine.RemoveAsync` 应重命名为 `RemoveIndexRecordAsync`，其语义仅为删除数据库记录和派生缓存。文件回收成功后才能调用它；回收失败不得提前移除索引。

### 3.2 命令状态机

破坏性流程必须遵循：

`Detected -> Reviewed -> Confirmed -> Executing -> Verified -> Completed/PartiallyFailed`

候选检测和推荐永远停留在 `Detected`，不能自动进入 `Confirmed`。确认模型保存本次操作快照，不根据易变的界面选中状态临时推导执行范围。

### 3.3 失败隔离

- 来源枚举失败不得启动 missing reconciliation。
- WebDAV 非成功状态、超时或权限错误必须向上传播为扫描失败。
- 单文件解码失败记录为可重试错误，不中断整个批次。
- 批量删除允许部分失败，但结果必须逐项可追踪。

## 4. 数据设计

### 4.1 图片身份

`images` 保留路径、大小、修改时间、SHA-256、感知哈希和状态。新增或规范：

- `content_hash_state`：pending/complete/failed。
- `derivative_version`：缩略图和识别模型版本。
- `last_seen_scan_id`：安全执行缺失对账。
- `deleted_utc` 不用于推断磁盘删除，只记录经核验的用户操作结果。

路径用于定位，SHA-256 用于确认内容身份。重新定位必须比较已有 SHA-256；若旧记录尚无哈希，先明确提示无法安全自动匹配，并提供重新扫描来源的路径。

### 4.2 重复候选

新增持久或会话级模型：

- `DuplicateGroup`：范围、检测算法、阈值、创建时间。
- `DuplicateCandidate`：图片 ID、精确/相似类别、相似度、推荐原因。
- `ReviewDecision`：undecided/keep/delete/defer，记录用户而非算法决策。
- `DeletionPlan`：确认快照、汇总、状态和逐项结果。

完全重复以 SHA-256 相等判断。视觉相似可使用 aHash/dHash，但 UI 和 API 必须返回算法类型与距离；任一阈值命中不能表述为“完全重复”。

### 4.3 搜索与索引

- 修正并实际使用 FTS5，通过 `MATCH` 查询文件名、相机、地点和可搜索标签。
- 对常用筛选和排序建立基于真实查询计划的复合索引，如 `(status, source_id, taken_utc, id)`。
- 所有列表查询使用 keyset 分页，返回 `totalCount`、`loadedCount` 和 continuation key。
- 标签、相册和 AI 建议变化后发布领域事件，使计数与搜索结果增量刷新。

## 5. 核心数据流

### 5.1 启动

1. 打开数据库并执行短时 schema migration。
2. 立即显示可交互 shell。
3. 查询聚合计数和首屏分页数据。
4. 后台加载目录摘要、任务状态和低优先级缩略图。

禁止在窗口显示前加载整个图库或为每个来源读取完整图片记录。

### 5.2 索引流水线

流水线分阶段执行：

1. 流式枚举并写入 scan staging。
2. 依据路径、大小和修改时间跳过未变化文件。
3. 快速读取元数据和嵌入预览，生成首屏缩略图。
4. 后台计算 SHA-256、感知哈希和识别标签。
5. 以 100–500 条短事务批量提交。
6. 仅当来源枚举完整成功时，根据 `last_seen_scan_id` 执行缺失对账。

CPU任务使用有界并行；本地磁盘、机械盘和 WebDAV 使用不同 I/O 并发策略。所有阶段接受取消令牌并报告阶段化进度。

### 5.3 缩略图

- 生成时直接按目标尺寸解码，避免完整 `Rgba32` 图像及多次克隆。
- 磁盘缓存键包含图片身份、尺寸和 `derivative_version`。
- 使用临时文件加原子替换，防止读取半成品。
- 内存采用有上限的 LRU，离屏请求可取消或降优先级。
- WPF 容器回收时解除旧请求，禁止属性 getter 无限创建 `Task.Run`。

### 5.4 WebDAV

- 使用 `ResponseHeadersRead` 流式写入临时缓存文件，避免 `GetByteArrayAsync`。
- 通过 ETag/mtime/size 做缓存失效和 single-flight 去重。
- 目录枚举流式返回，使用小规模有界并发。
- 缓存具有容量预算和 LRU 清理。
- 任何枚举错误都必须阻止缺失对账。

### 5.5 重复审阅与删除

1. 检测服务只产生候选和推荐原因。
2. 用户在 UI 中显式设置每张图片的决定。
3. 应用构造不可变 `DeletionPlan` 并展示完整汇总。
4. 用户确认后调用 `IUserAuthorizedFileOperation` 移至回收站。
5. 每个成功项验证原路径不再存在或由系统返回成功。
6. 仅对成功项更新索引和缩略图；失败项保留并展示原因。
7. 记录操作结果，以支持恢复说明和诊断。

## 6. UI 架构

- 引入统一 `PhotoGrid` 控件供目录、搜索和标签视图复用。
- 使用支持回收的虚拟化多列布局；重复详情同样必须虚拟化或分页。
- ViewModel 只暴露异步、可取消命令；运行期间通过 `CanExecute` 禁用冲突动作。
- 搜索使用可取消 debounce：新查询取消旧查询，结果就绪后批量切换。
- 导航状态、筛选 chips、选中项和滚动位置在视图切换后保持。
- 日志与用户通知分离：通知描述结果和下一步，日志保存诊断细节。

## 7. 安全与隐私

- 默认本地处理图片和识别结果。
- 凭据不得写入日志或普通配置文件；使用 Windows Credential Manager 或 DPAPI。
- 所有 SQL 参数化。
- 规范化并验证文件路径，防止来源越界及 TOCTOU 风险。
- 发送图片或元数据到外部服务前必须获得独立、清晰的用户授权。

## 8. 可观测性

定义结构化事件和阶段计时：

- 启动：shell、数据库、首屏、可交互。
- 索引：enumerate/hash/metadata/decode/thumbnail/recognition/database。
- 搜索：解析、SQL、对象化、首批展示。
- UI：缩略图队列、取消、缓存命中、长帧。
- 文件操作：计划 ID、逐项结果和冲突；不得记录凭据。

## 9. 测试策略

### 9.1 功能与安全测试

- 索引、搜索、标签和检测不改变原文件内容、名称、时间戳或位置。
- 完全重复与视觉相似分类正确且文案一致。
- 未确认、取消和关闭窗口均不触发文件操作。
- 删除计划只处理用户明确选择的项，并默认进入回收站。
- 回收失败时索引仍存在；成功后索引才更新。
- 重新定位覆盖相同哈希、不同哈希、损坏文件、无权限和文件竞态。
- WebDAV错误不得导致全源误标缺失。

### 9.2 规模与性能测试

固定构造 1千、10万、100万图片库，覆盖小 JPEG、大 JPEG、RAW、损坏文件、本地 SSD、HDD 和可注入延迟/错误的 WebDAV。使用 EventSource、PerfView/WPA、SQLite `EXPLAIN QUERY PLAN` 和进程内存指标验证产品性能预算。

当前实现使用容量4的分析通道、按CPU数量限制为1–4个读取/解码工作器、单数据库写入器及100条短事务。搜索由 FTS 命中集驱动连接图片表；基准结果见 `PERFORMANCE_BASELINE_2026-08-27.md`。桌面测试通过 `PICER_DATA_ROOT` 使用隔离目录，并验证真实进程 UI Automation 与 Windows 回收站边界。

### 9.3 发布门禁

- 全部单元与集成测试通过。
- P0 安全场景必须有自动化回归测试。
- 10万图片基准不超过性能预算；超标须记录批准的例外。
- 无“删除文件”文案映射到仅删除索引记录的实现。

## 10. 迁移策略

1. 先修正文案并禁用当前自动批处理，避免继续产生错误预期。
2. 重命名索引删除 API，建立编译期语义边界。
3. 引入删除计划和回收站服务，完成安全回归测试。
4. 再实施分页搜索、异步启动和索引流水线，减少一次性重构风险。

数据库使用 `PRAGMA user_version`：v2 重建 FTS5 内容，v3 增加 `rejected_suggestions` 和稳定拍摄时间排序索引。迁移前发布流程复制 `picer.db` 及同目录 WAL/SHM（应用关闭状态）；失败时关闭应用并整体恢复这三个文件。若仅回退 v3 代码，可执行 `DROP TABLE IF EXISTS rejected_suggestions; DROP INDEX IF EXISTS idx_images_status_taken_id; PRAGMA user_version=2;`，这些对象不包含原图片，也不影响用户标签。v2 涉及 FTS 重建，禁止原地降级，必须恢复迁移前备份；原始图片始终不参与数据库回滚。

## 11. 本地人物与复合视觉检索

### 11.1 组件边界

新增组件：

- `Picer.Core.Vision.ModelRuntime`：模型加载、执行提供程序、会话复用和内存预算。
- `Picer.Core.Vision.FaceDetector`：输出人脸边界、关键点和质量分，不处理真实姓名。
- `Picer.Core.Vision.FaceEmbedder`：对齐人脸并输出归一化特征向量。
- `Picer.Core.Vision.FaceClusterer`：形成候选人物组，不产生已确认身份。
- `Picer.Core.Vision.VisualTagger`：输出场景、服饰和物体建议及置信度。
- `Picer.Core.People.PeopleService`：人物命名、别名、合并、拆分、忽略和纠错。
- `Picer.Core.Search.CompositeQueryParser`：解析人物、地域、时间和视觉谓词。
- `Picer.Core.Search.CompositeSearchEngine`：参数化查询、集合求交、分页和解释。
- `Picer.App.People`：人物工作台和候选审阅界面。

模型运行时通过接口注入：

```csharp
public interface IFaceAnalyzer
{
    Task<IReadOnlyList<DetectedFace>> AnalyzeAsync(
        ImageAnalysisInput input,
        CancellationToken cancellationToken);
}

public interface IVisualTagger
{
    Task<IReadOnlyList<VisualPrediction>> PredictAsync(
        ImageAnalysisInput input,
        CancellationToken cancellationToken);
}
```

模型 manifest 记录文件哈希、许可证、输入尺寸、归一化参数、输出语义和模型版本。未通过许可证及权重再分发审查的模型不得进入安装包。

### 11.2 数据模型

下一 schema 版本新增派生表，不修改原 `images` 内容：

```sql
CREATE TABLE people (
    id INTEGER PRIMARY KEY,
    display_name TEXT,
    state TEXT NOT NULL,          -- candidate/confirmed/hidden
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);

CREATE TABLE person_aliases (
    person_id INTEGER NOT NULL REFERENCES people(id) ON DELETE CASCADE,
    alias TEXT NOT NULL COLLATE NOCASE,
    PRIMARY KEY (person_id, alias)
);

CREATE TABLE face_instances (
    id INTEGER PRIMARY KEY,
    image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
    person_id INTEGER REFERENCES people(id) ON DELETE SET NULL,
    box_x REAL NOT NULL, box_y REAL NOT NULL,
    box_width REAL NOT NULL, box_height REAL NOT NULL,
    quality REAL NOT NULL,
    embedding BLOB NOT NULL,
    embedding_dimension INTEGER NOT NULL,
    model_version TEXT NOT NULL,
    assignment_state TEXT NOT NULL, -- candidate/confirmed/rejected/ignored
    confidence REAL,
    UNIQUE(image_id, box_x, box_y, model_version)
);

CREATE TABLE visual_predictions (
    image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
    concept_id TEXT NOT NULL,
    confidence REAL NOT NULL,
    model_version TEXT NOT NULL,
    decision TEXT NOT NULL,       -- suggested/accepted/rejected
    created_utc TEXT NOT NULL,
    PRIMARY KEY(image_id, concept_id, model_version)
);

CREATE TABLE analysis_state (
    image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
    analyzer TEXT NOT NULL,
    model_version TEXT NOT NULL,
    source_fingerprint TEXT NOT NULL,
    state TEXT NOT NULL,          -- pending/running/complete/failed
    error_code TEXT,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY(image_id, analyzer, model_version)
);
```

人脸框使用相对坐标。向量记录维度和模型版本，不同版本禁止直接比较。候选人物的 `display_name` 允许为空，不能用模型生成名称填充。

多人照片采用 `images 1:N faces` 建模，每个 `face_id` 独立关联人物。检测结果按人脸框中心点的 `y/x` 稳定排序后生成 `ordinal`，UI选择状态使用 `face_id`，不得依赖易变的列表序号。人物筛选使用 `EXISTS` 返回去重后的照片；多人物 AND 查询为每个人物生成独立 `EXISTS` 子句，确保要求同一张照片同时包含所有目标人物。

自动聚类需增加同图排斥约束：同一 `image_id` 下的不同 `face_id` 默认不能进入同一候选人物组。用户确认可以覆盖该约束，但必须保留纠错记录。重新检测导致人脸ID变化时，通过框重叠、关键点距离和向量相似度迁移已确认归属；无法唯一对应的归属进入待审阅，不得静默丢失或错误转移。

索引包括 `face_instances(person_id, assignment_state, image_id)`、`person_aliases(alias)`、`visual_predictions(concept_id, decision, confidence, image_id)` 和 `analysis_state(state, analyzer, model_version)`。

### 11.3 分析流水线

视觉分析与文件索引解耦：

`enumerate -> bounded decode -> detect -> align in memory -> embed -> visual tag -> batch persist -> cluster/reconcile`

- 通道容量有界；解码图像在单项完成后立即释放。
- CPU 默认并发 1–2，硬件加速仍受独立内存信号量限制。
- 优先当前可见、用户选中和最近新增图片；全库回填为低优先级。
- `source_fingerprint` 由内容身份、分析输入和模型版本组成；一致时跳过。
- 以 50–100 张短事务提交；取消回滚当前事务，已完成批次保持一致。
- 模型升级写入新版本结果，覆盖验证通过后才清理旧派生数据。

聚类输出仅写入候选关系。已确认人物原型与聚类中心分离，重聚类不得覆盖用户命名、拆分、合并或拒绝决定。

### 11.4 匹配与阈值

人脸匹配使用归一化向量余弦相似度。阈值由固定评测集针对具体模型版本校准，不得跨模型硬编码：

- `auto_candidate_threshold`：进入人物候选组。
- `review_threshold`：低于该值保持未知；高于该值仍需确认。
- `duplicate_face_threshold`：同图检测框去重。

首版不自动确认身份。界面置信度是相似度解释，不是身份概率。多人照片分别关联，最终以 `image_id` 去重。

### 11.5 视觉标签本体

内部使用稳定 `concept_id`，例如 `apparel.dress`、`scene.mountain`、`activity.hiking`；界面名称可本地化。模型原始标签先映射到受控本体，无法映射的输出不进入自然语言查询。

视觉建议与用户标签分开保存。用户接受、拒绝或修正优先于模型输出。季节根据 GPS 半球和拍摄月份计算：北半球 6–8 月、南半球 12–2 月为夏季；无 GPS 时使用资料库默认地区并显示推断依据。

### 11.6 复合查询计划

解析器输出结构，不直接执行自由文本：

```csharp
public sealed record CompositePhotoQuery(
    IReadOnlyList<PersonPredicate> People,
    GeoPredicate? Geography,
    TimePredicate? Time,
    IReadOnlyList<VisualPredicate> Visual,
    QueryBooleanOperator Operator,
    IReadOnlyList<UnsupportedClause> Unsupported);
```

“老婆在商洛，夏天穿裙子，在山上”的计划：

1. 通过 `person_aliases` 解析人物 ID；别名冲突时请求选择，不猜测。
2. 地名解析为缓存的行政区边界，以 EXIF GPS 过滤。
3. 根据地点半球和拍摄月份生成季节谓词。
4. 使用 `visual_predictions` 过滤 `apparel.dress` 与 `scene.mountain`。
5. 按 `image_id` 求交、排序和 keyset 分页，并计算各子条件命中数。
6. 返回 `QueryExplanation`：解析结构、逐项计数、交集缩减、模型版本和置信度。

不确定或不支持的片段进入 `UnsupportedClause`。UI 必须提供删除条件、选择人物或改写查询，不返回伪装的零结果。

### 11.7 隐私与威胁模型

- 人脸向量、人物名称和别名属于敏感本地数据，日志、遥测和异常文本不得记录其值。
- 默认禁用远程人脸和视觉端点；完整流程必须通过禁网测试。
- 模型文件安装时校验 SHA-256 和 manifest，阻止被替换的模型执行或外发数据。
- 数据导出让用户选择是否包含人物识别数据并显示风险说明。
- “清除人物识别数据”和“清除视觉建议”分开执行，均不得删除 `images` 或原文件。
- 人脸裁剪只允许存在于受限临时内存/临时文件，取消、退出和崩溃恢复后清理。

### 11.8 模型与硬件策略

模型选型进入独立技术评审，比较准确率、包体、CPU 延迟、内存、ONNX 兼容性、许可证和权重再分发条款。运行时优先本地 ONNX CPU；可选硬件提供程序失败时回退 CPU，并显示执行设备。

目标硬件预算为 16 GB 系统内存和 2 GB 显存。2 GB 显存仅作为 DirectML 可选加速层：固定 batch=1、固定输入尺寸、单会话串行推理，加载失败、超时、显存不足或算子回退过多时自动切换 CPU。GUI、缩略图与搜索不等待视觉模型初始化。

量化策略按模型和执行提供程序分别决定：YuNet/SFace 不量化；CLIP 比较 FP16 与 INT8；图片描述模型比较 FP16、INT8 和 INT4 权重量化。INT4 仅对受支持的 `MatMul`/`Gather` 权重生效，不等同于全模型 4 位。由于 ONNX INT4 可能要求 opset 21，而当前 DirectML 支持上限为 opset 20，INT4 默认使用 CPU，只有通过完整算子兼容性测试后才允许启用 GPU。

内存软预算：GUI 与缩略图缓存 2 GB、基础索引与人脸任务 2 GB、图片描述独立进程 6 GB，并至少为系统保留 4 GB。图片描述并发数默认为 1。

模型下载不是建库的隐式副作用。首次启用时展示大小、来源、许可证和哈希并取得确认；离线环境支持导入经校验的模型包。

### 11.9 测试与发布门禁

- 单元：坐标变换、向量归一化、阈值边界、别名、季节半球、查询解析和 SQL 参数化。
- 集成：模型版本并存、断点续作、取消、迁移/回滚、清除派生数据不影响原图。
- 评测集：覆盖年龄跨度、遮挡、侧脸、多人、低照度和相似家庭成员，并取得授权。
- 分别报告检测召回、聚类纯度、错误合并率、错误归人率和未知拒识率；错误归人回归阻止发布。
- UI 自动化：命名、别名、合并、拆分、纠错、零结果解释和条件放宽。
- 隐私：禁网完整运行；代理断言无图片、人脸、人物名称或复合查询全文外发。
- 性能：1 万/10 万图片吞吐、取消延迟、峰值内存、前台滚动和复合查询 p50/p95。

### 11.10 迁移与回滚

人物与视觉表使用独立 schema 版本。迁移前沿用数据库/WAL/SHM 整体备份。旧版代码可保留未知新表；清理时只删除派生表和索引。迁移、回滚或模型升级均不得扫描后写回、移动或删除原始图片。

### 11.11 人物审阅交互

- 人物详情集中展示名称、关系别名和已确认照片；双击照片进入多人逐脸审阅。
- 已确认错误的人脸可移动到另一人物或标记“不是此人”，纠错约束优先于后续自动匹配。
- 候选组只是一种建议；用户可先拆出错误成员，再确认剩余整组。
- 人物合并必须二次确认，迁移已确认人脸和别名并记录纠错历史，不修改任何原照片。
- 所有人物云端分析上下文保持匿名，不发送本地姓名或亲属关系。

### 11.12 人工标注学习与回归门禁（规划）

当前人物学习只调整 SFace 向量之上的决策层：多外观原型、困难负样本、按人物自适应边界、质量门槛和候选差距；它不修改 YuNet 或 SFace 权重。用户确认是不可覆盖的事实，自动归类只维护建议。

后续训练必须使用版本化数据快照，将同一原图衍生物、近重复连拍和同一视频的全部关键帧放在同一分区，严格隔离训练集与验证集。候选策略或模型先以只读影子模式比较检测召回、身份精确率/召回率、错误接受/拒绝和未知拒识；通过门禁并由用户明确应用后，才允许后台重算建议。策略与模型产物必须可追溯、可取消、可回滚，不得改变人工归属、目录树、原始媒体或其他索引标签。

时间、地点、相册、场景和共同出现人物只能参与可解释的推荐排序，不能单独确认身份。照片与视频共享标注和人物策略，同时避免同一视频帧跨训练/验证集合泄漏。详细任务、数据实体、指标默认值、回归用例及实施顺序见 [人工标注驱动的人脸学习路线](FACE_LEARNING_ROADMAP_2026-09-03.md)。

## 12. AI 问答文本检索与完整记录取回（规划）

### 12.1 组件边界

新增以下本地组件：

- `AiTextProjectionService`：从照片、场景、视频、相册、人物、地点等规范业务表投影可检索文档。
- `AiTextRepository`：保存文档正文、来源、状态、内容哈希和稳定定位符。
- `Bm25TextIndex`：管理 SQLite FTS5 文本索引、中文词法规范化和 BM25 查询。
- `AiEvidenceHydrator`：根据命中文档定位符，从规范业务表取回完整记录及有限关联上下文。
- `LocalQuestionAnsweringService`：调用本地语言模型进行候选复排、事实判断和答案生成。
- `QuestionEvidenceNavigator`：把回答依据映射回照片、场景节点、视频、相册或人物 UI。

索引层只保存可重建投影；完整照片描述、场景总结和用户确认结果仍以现有业务表为事实来源。大模型不得直接执行 SQL，也不得接收数据库路径或本地文件系统路径。

### 12.2 统一文档模型

建议新增规范表：

```sql
CREATE TABLE ai_text_documents (
    id INTEGER PRIMARY KEY,
    source_type TEXT NOT NULL,       -- photo/scene/video/transcript/album/person/location/ocr/note
    source_id TEXT NOT NULL,         -- 来源表稳定主键或稳定指纹
    parent_type TEXT,                -- image/scene/video/album/person/source
    parent_id TEXT,
    title TEXT NOT NULL DEFAULT '',
    body TEXT NOT NULL,
    keywords TEXT NOT NULL DEFAULT '',
    lexical_text TEXT NOT NULL,      -- 使用当前 tokenizer_version 预处理后的检索文本
    state TEXT NOT NULL,             -- active/rejected/stale/deleted
    provider TEXT,
    model_version TEXT,
    prompt_version TEXT,
    content_hash TEXT NOT NULL,
    tokenizer_version TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    UNIQUE(source_type, source_id)
);

CREATE VIRTUAL TABLE ai_text_fts USING fts5(
    title, lexical_text, keywords,
    content='ai_text_documents', content_rowid='id',
    tokenize='unicode61 remove_diacritics 2'
);

CREATE INDEX idx_ai_text_source ON ai_text_documents(source_type, source_id, state);
CREATE INDEX idx_ai_text_parent ON ai_text_documents(parent_type, parent_id, state);
CREATE INDEX idx_ai_text_hash ON ai_text_documents(content_hash, tokenizer_version);
```

`source_id` 必须足以重新取回一条完整规范记录。例如照片使用 `image_id + provider + model_version` 或当前有效描述 ID，场景使用稳定 fingerprint，视频使用 `video_id + summary_version`。不得使用列表序号、树节点对象地址或可能变化的显示名称定位。

每个媒体文档还需要规范化的结构化标签投影，不能只把标签拼接成不可解释的正文。建议增加：

```sql
CREATE TABLE ai_document_facets (
    document_id INTEGER NOT NULL REFERENCES ai_text_documents(id) ON DELETE CASCADE,
    facet_type TEXT NOT NULL,       -- person/scene/geo/time/directory/album/media/device/tag
    facet_key TEXT NOT NULL,        -- 稳定ID或受控层级键
    facet_value TEXT NOT NULL,      -- 用于显示和词法检索的值
    state TEXT NOT NULL,            -- confirmed/accepted/derived/rejected/stale
    confidence REAL,
    origin TEXT NOT NULL,           -- user/metadata/model/system
    PRIMARY KEY(document_id, facet_type, facet_key, facet_value)
);

CREATE INDEX idx_ai_facets_lookup
ON ai_document_facets(facet_type, facet_key, state, document_id);
```

照片文档至少投影以下 facet：

- `person`：已确认 `person_id`、规范名称和别名；候选人物不进入 confirmed 事实；
- `scene`：照片场景、已接受场景 fingerprint、场景名称；
- `geo`：国家到村镇的每一级稳定层级键，以及用户修正地点；
- `time`：年、月、日、ISO 周、季节、时间线相册和时间锚点；
- `directory/album`：来源、目录层级、普通相册和时间相册；
- `media/device/tag`：照片类型、相机设备、格式、用户标签和已接受模型标签。

视频文档采用同一 facet 模型：

- 视频文件本身的时间、地域、目录、设备和用户标签；
- 关键帧已确认人物，汇总为视频级 `person` facet，同时保留关键帧 ID 和时间码关联；
- 关键帧场景、视频整体场景、视频摘要和转写关键词；
- 视频所属相册、时间线与场景分类。

为支持精确依据定位，视频关键帧关联建议单独保存：

```sql
CREATE TABLE ai_video_evidence_points (
    document_id INTEGER NOT NULL REFERENCES ai_text_documents(id) ON DELETE CASCADE,
    video_id INTEGER NOT NULL,
    keyframe_id INTEGER,
    timestamp_ms INTEGER NOT NULL,
    evidence_type TEXT NOT NULL,    -- person/scene/transcript/description
    evidence_key TEXT NOT NULL,
    PRIMARY KEY(document_id, timestamp_ms, evidence_type, evidence_key)
);
```

FTS 表使用触发器或同事务显式维护。业务表更新、统一文档更新和 FTS 更新必须处于同一短事务；崩溃后通过内容哈希审计和投影重建恢复一致性。

### 12.3 中文词法与 BM25

SQLite `unicode61` 不提供完整中文分词，因此写入 `lexical_text` 前由应用执行确定性的中文切词与规范化：

- 中文词、行政区划、人物别名、相机型号、日期和文件名分别保留；
- 英文统一大小写，连字符和下划线同时保留原词与拆分词；
- 查询必须使用与文档相同的 `tokenizer_version`；
- 分词器升级时后台增量重建 `lexical_text` 和 FTS，不修改规范正文；
- 用户输入必须转义为参数化 FTS 查询，禁止拼接未受控 `MATCH` 表达式。

首阶段查询使用 FTS5 `bm25(ai_text_fts, ...)` 取 Top-K。SQLite BM25 分数越小相关性越高，排序实现和测试必须固定这一语义。建议默认召回 50 条，经来源状态、用户筛选和父对象去重后，向本地模型提供 8–20 条完整证据；具体数字由性能基准调整。

### 12.4 检索增强问答数据流

```text
question
  -> 本地意图/过滤条件解析
  -> 人物/地域/时间线/目录/相册/媒体类型 facet 过滤
  -> 查询词规范化与中文分词
  -> FTS5 BM25 Top-K
  -> 状态、人物、时间、地点、目录等结构化过滤
  -> 按 source_type/source_id 取回完整规范记录
  -> 按 parent_id 补充有限场景/视频上下文并去重
  -> 本地语言模型复排、判断和生成答案
  -> 带稳定定位符的 AnswerEvidence[]
  -> UI 点击定位
```

建议的数据契约：

```csharp
public sealed record AiTextHit(
    long DocumentId, string SourceType, string SourceId,
    string? ParentType, string? ParentId, double Bm25Score);

public sealed record AiEvidence(
    string SourceType, string SourceId, string Title, string FullText,
    DateTime? EffectiveTime, IReadOnlyDictionary<string, string> Metadata);

public sealed record AiAnswer(
    string Text, IReadOnlyList<AiEvidenceReference> Evidence,
    IReadOnlyList<string> UnsupportedClauses);
```

BM25 命中片段只用于调试和 UI 高亮，不得代替 `FullText`。`AiEvidenceHydrator` 必须重新查询来源表，确认记录仍有效，并返回完整正文。场景证据可包含场景完整总结及代表照片引用；照片证据包含完整照片描述；视频证据包含完整视频摘要并按预算选择转写片段。

查询计划应区分结构化标签与自由文本：人物、行政区划、时间、目录、相册和媒体类型优先解析为 facet 条件；场景描述、活动、物体、摘要和转写内容进入 BM25。无法唯一解析的人物别名或地域名称必须返回歧义供用户选择，不得让本地模型猜测稳定 ID。

照片和视频统一返回 `AiEvidence`，但导航目标不同：照片定位到 `image_id`；场景定位到 fingerprint 及其照片集合；视频定位到 `video_id`，若命中关键帧人物、场景或转写片段则同时返回 `timestamp_ms`。

### 12.5 投影与增量更新

各业务服务在成功提交规范数据后发布本地领域事件，例如：

- `PhotoDescriptionChanged`
- `SceneDescriptionAccepted`
- `VideoSummaryChanged`
- `TranscriptChanged`
- `AlbumChanged`
- `PersonNameChanged`
- `LocationHierarchyChanged`
- `DerivedTextRejectedOrDeleted`
- `FaceAssignmentConfirmedOrMoved`
- `SceneClassificationChanged`
- `TimelineAnchorChanged`
- `MediaTagChanged`
- `VideoKeyframeEvidenceChanged`

事件处理器按 `(source_type, source_id)` 幂等 upsert。计算规范化正文的 SHA-256；哈希和 tokenizer 版本均未变化时跳过 FTS 写入。应用异常退出后，启动审计只处理业务表 `updated_utc` 晚于投影的记录，不阻塞首窗显示。提供“重建 AI 文本索引”维护任务，可取消、可显示进度，并且不重新调用任何视觉或语言模型。

facet 与正文投影必须在同一个文档更新事务内替换。人物纠错、照片移出场景、地点批量编辑、相册删除或时间修正后，旧 facet 必须立即失效。关键帧人物移动到其他人物类别时，同时更新关键帧证据点和视频级人物汇总；只有视频中不再存在该人物的已确认关键帧时，才移除视频级人物 facet。

### 12.6 本地模型上下文与隐私

- AI 问答最终判断使用本地语言模型，默认断网可运行。
- 只向模型提供检索命中的有限完整证据，不发送整库文本。
- 人物名称、别名和人脸关联属于敏感本地数据；只在用户问题确实需要且本地模型运行时加入上下文。
- 日志只记录文档 ID、来源类型、候选数、耗时和错误码，不记录问题全文、人物名称或完整证据正文。
- 若未来增加云端问答，必须作为独立功能重新授权，不得复用云端照片分析授权。

### 12.7 性能与并发

- 10 万条文本投影 BM25 查询 p95 目标小于 150 ms，100 万条小于 350 ms。
- 完整证据取回 p95 目标小于 100 ms；本地模型首 token 时间单独统计。
- FTS 写入按小批次短事务执行，优先级低于当前可见照片加载和用户发起查询。
- 新问题取消旧问题的 BM25、证据取回和本地推理；旧任务不得覆盖新问题 UI。
- 上下文按字符/token 预算截断关联记录，但单条被选中的规范正文不得静默截断；超长正文应分段索引并保留指向完整来源的定位符。

### 12.8 测试与迁移

- 单元测试：中文分词版本、FTS 转义、BM25 排序方向、内容哈希、状态过滤、稳定定位符解析。
- 集成测试：照片描述、场景总结、视频摘要新增/更新/拒绝/删除后的增量投影与 FTS 一致性。
- 标签投影测试：人物确认与移动、场景重分类、地域层级修正、时间线变化、相册删除后，新旧 facet 与照片/视频结果一致。
- 视频测试：摘要、转写和关键帧人物/场景均可召回同一视频，去重后保留最佳证据点和正确时间码。
- 回归测试：BM25 命中后取回完整正文而非截断片段；回答证据可以定位原 UI 对象。
- 隐私测试：断网问答成功；日志和异常包不包含问题全文、人物名称或完整正文。
- 性能测试：10 万/100 万文档查询、并发索引更新、快速连续提问取消和峰值内存。

该功能需要新的数据库 schema 版本。迁移只创建可重建派生表并从现有业务表后台回填；迁移失败时删除新表并恢复旧版本号，不修改或删除照片、现有描述、场景总结、视频摘要及用户数据。

## 13. Qwen3-ASR-1.7B 本地视频转写迁移设计

### 13.1 模型与处理管线

目标模型固定为 `Qwen3-ASR-1.7B`。标准管线为：FFmpeg 无损提取或转换为模型支持的单声道 PCM → 宽松 VAD 分段 → 带重叠窗口的分段识别 → 时间顺序合并与重复文本消除 → 可选 `Qwen3-ForcedAligner-0.6B` 对齐 → 质量检查 → 原子保存。

VAD 不能成为内容过滤器。首尾保留缓冲区，相邻片段保留重叠；阈值和最短语音长度必须配置化并写入处理版本。即使 VAD 没有发现语音，也必须执行一次整段音频转写。分段转写为空、明显重复或低可信时，同样执行整段兜底；只有分段与整段结果均不可用时才报告“未可靠识别”，不得把空结果写成成功缓存。

### 13.2 数据与增量兼容

转写缓存键至少包含 `video_id`、源视频大小与修改时间、音轨指纹、ASR 模型版本、解码参数、VAD 配置版本及对齐器版本。旧 Whisper 结果继续可读且不得在新模型失败时删除。Qwen3-ASR 新结果通过质量门禁后，以短事务切换为当前有效版本，并发布 `TranscriptChanged` 事件增量更新 AI 文本检索索引。

建议转写记录保存：完整文本、语言、分段起止时间、可选字词时间戳、模型与处理版本、是否触发整段兜底、质量状态、创建时间和错误代码。日志只记录视频 ID、阶段、耗时、音频时长和错误代码，不记录完整语音文本。

### 13.3 运行与回退

模型在本地进程或本机服务中运行，不向云端上传音频。启动时检查模型、推理运行时、GPU/CPU 能力与可用内存；资源不足或新模型异常时，可显式回退到随包 Whisper 旧链路，但 UI 必须显示实际使用的模型和回退原因。任务支持取消、断点续传和有限并发，媒体转写优先级低于当前可见内容加载。

### 13.4 验收与回归

- 固定语料覆盖普通话、方言、低音量、背景音乐、笑声包围短对白、长静音后讲话、多人交叠和纯无语音视频。
- 对同一批音轨比较 Qwen3-ASR-1.7B 与当前 Whisper `base-q5_1` 的字错率、整句漏检率、重复幻觉率、耗时和峰值内存。
- “笑声—短对白—笑声”及“长静音—短对白”不得因 VAD 返回空结果；必须能观察到整段兜底是否执行。
- 模型升级、VAD 配置变化和音轨变化只重建受影响视频转写，不重建照片、人物、目录树或其他视频派生数据。
- 新转写失败、取消或进程崩溃后，旧转写和检索索引保持可用且数据库无半成品状态。
