# 索引服务与 GUI 客户端分离（C/S）架构设计提案

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
> 后续替代：IndexService 已提供独立 GUI/Agent、授权同步、分析、修复和索引清除接口；“仅只读网关”“远程人工修改不回传”“等待批准”的表述均为本阶段背景。原始媒体仍只读，索引清除不等于删除原文件，远端能力仍受配置及授权约束。
<!-- /historical-status-2026-10-10 -->

## 2026-10-08 剩余代码与目录描述更新

六项代码收尾及照片/视频递归增量描述已实现，详见 [实施与验证报告](REMAINING_CODE_IMPLEMENTATION_2026-10-08.md)。本文较早的 P2 后续代码清单为历史状态；内容级存储保留记录 ID 兼容投影，ViewModel 通过注入的网关与镜像服务工作。实际交付与回归结果以新报告为准，质量和硬件验收门禁仍单独保留。

状态：设计提案，等待用户批准；批准前不实施
日期：2026-09-08
关联：[SDD](SDD.md)（架构总纲）、[统一模型平台路由](UNIFIED_MODEL_PLATFORM_ROUTING_2026-09-07.md)、[发布准备状态](RELEASE_READINESS_2026-09-07.md)

## 1. 背景与现状

当前 Picer 是**单体架构**：`Picer.App`（WPF）进程内直接实例化 `Picer.Core` 的全部服务（`AppServices.Initialize`），索引引擎、SQLite（`picer.db`）、缩略图缓存、视觉分析都与 GUI 同进程、同生命周期：

- GUI 关闭即索引停止：批量扫描、哈希、识别、转写等长任务随窗口退出而中断。
- SQLite 文件被 GUI 独占：未来任何第二个客户端（另一台电脑、手机、Web 壳）都无法访问同一图库。
- `server/Picer.ModelServer` 是独立进程，但只承载训练任务、模型目录与健康探针；`/api/index/v1/records` 仅为内存占位，无客户端消费，与真实索引无关。
- 索引/搜索/缩略图当前**没有任何 HTTPS 服务化通道**。

## 2. 目标与非目标

### 2.1 定位与目标

产品定位：索引服务提供照片的**逻辑视图**——搜索、查询、管理（标签/人物/相册/场景）——对原始照片**基本只读，不承担删除能力**。用户对照片的增加、删除、移动都是服务外部发生的客观事实，服务必须稳定应对，并以内容哈希为锚点重建关联。

1. 索引成为独立服务进程（`Picer.IndexService`），GUI 变为纯客户端，通过 **HTTPS** 通信。
2. **索引构建（枚举、哈希、元数据、缩略图、视觉推理、转写）全部在索引服务完成**；GUI 零构建逻辑，只触发任务、消费结果、写入逻辑数据（标签/人物/相册）。
3. 服务生命周期与 GUI 解耦：GUI 关闭后扫描、识别、转写继续运行；GUI 重开即时恢复现场。
4. **支持合一与分离两类部署，逻辑始终分离**：单进程合一（`local`，进程内直调）、同机双进程（服务+GUI，命名管道等非网络协议）、跨机分离（HTTPS）。部署形态只是传输选择，不改变职责边界。
5. 同一图库支持多个 GUI 客户端并发访问（同机或局域网内其他机器，远程为可选授权）。
6. 数据目录（SQLite、缩略图、视频缓存）归服务所有，可部署在与 GUI 不同的磁盘/机器上（配合已完成的 `picer.paths.json` 相对路径配置机制）。
7. **内容哈希是照片身份锚点**：照片被移动、改名、换来源重新导入后，凭 SHA-256 重新关联既有索引信息（标签、人物、相册、场景、分析结果），不重复哈希计算与模型推理，只重建物理元数据（路径、来源、时间戳、缺失状态）。
8. 保持 SDD 不变量：只读边界、参数化 SQL、离线可运行、隐私日志约束。

### 2.2 非目标（本期不做）

1. 不做云端索引、不做账号体系、不做互联网直连服务。
2. **服务不提供任何修改、移动、删除原始文件的端点或能力**。删除若发生，只能是文件所有者侧（本机 GUI 对本机文件）的外部事实，服务通过上报与扫描对账感知。
3. SQLite schema 需为"内容身份层"做一次受控演进，仍走 `PicerDatabase` 的 `PRAGMA user_version` 迁移链，附迁移/回滚方案。
4. 不在本提案内实现 `ModelServer` 的 `/api/index/v1`（那是"应用↔平台"的元数据同步协议，与本文"GUI↔索引服务"是两条通道，命名上明确区分）。
5. 不引入 gRPC/二进制协议；与 ModelServer 一致采用 JSON REST，便于复用现有探针与诊断工具。

## 3. 总体架构

```text
┌─────────────────────────────┐          HTTPS (loopback / LAN)          ┌──────────────────────────────┐
│ Picer.App (WPF 客户端)       │ ────────────────────────────────────────▶ │ Picer.IndexService (ASP.NET) │
│  - ViewModels / PhotoGrid   │   GET  /api/gateway/v1/images?cursor=…   │  - 引用 Picer.Core           │
│  - IIndexGateway (接口)     │   POST /api/gateway/v1/search            │  - 构建: IndexEngine/视觉/转写│
│    ├ LocalGateway(现状直调)  │   GET  /api/gateway/v1/thumbs/{id}/{ver} │  - PicerDatabase (独占)      │
│    └ HttpGateway(新增)      │   POST /api/gateway/v1/verified-changes  │  - ThumbnailService          │
│  - 本地缩略图磁盘缓存        │   POST /api/gateway/v1/scan              │  - JobManager 后台任务调度    │
│  - 回收站执行(仅本机文件)    │   Bearer token + 证书固定(TOFU)           │  - 数据根目录可配置           │
└─────────────────────────────┘                                          └──────────────────────────────┘
        零索引构建逻辑；确认与回收站仅限本机文件                      构建/存储/查询唯一所有者
```

关键原则：

1. **SQLite 唯一所有者是服务进程**。客户端永不直接触碰 `picer.db`；这同时避免 SQLite 在网络文件系统上的损坏风险（明确排除"GUI 直连远程 SQLite 文件"方案）。
2. **构建逻辑只存在于服务**（service 模式）：枚举、哈希、元数据、缩略图、视觉推理、转写全部在服务进程执行，GUI 在 service 模式下零构建逻辑；`local` 模式（回归基线）维持现状由 GUI 进程内构建。两种模式共用同一份 Core 代码，任一时刻只有一个构建者，禁止双写。
3. **API 处理器传输无关**：端点写成 DTO 进/出的处理器函数，Kestrel（HTTPS）、命名管道帧、进程内直调共享同一实现；新增传输不新增业务代码。
4. **客户端通过接口切换后端**：`AppServices` 依赖一个新接口 `IIndexGateway`；实现族覆盖三种部署形态（直调/管道/HTTPS），模式由配置选择，分离部署为显式 opt-in——对齐"远程/分离功能默认关闭"的隐私原则。

## 4. 服务端设计

### 4.1 构建职责归属（已确认）

**决策：索引构建在索引服务中完成。服务不是"只保存和提供"的存储，而是拥有完整构建流水线的引擎宿主；GUI 在 service 模式下不执行任何构建逻辑。**

| 职责 | 归属 | 说明 |
|---|---|---|
| 来源枚举、变更检测（增/删/移动）、缺失对账 | 服务 | 扫描以服务为唯一执行者，多 GUI 客户端共享同一进度 |
| SHA-256 哈希、感知哈希 | 服务 | 身份锚点计算与内容层维护（§4.5） |
| 元数据提取、缩略图生成 | 服务 | 派生数据服务侧一次性生成，客户端仅缓存展示 |
| 视觉推理（YuNet/SFace、CLIP、Florence）、视频转写/关键帧 | 服务 | 服务打包 `third_party/runtime`（模型、FFmpeg、whisper），复用 `RuntimeComponentService` 哈希门禁 |
| 逻辑数据写（用户标签、人物确认、相册） | 客户端发起，服务持久化 | 参数化 SQL 只在服务端 |
| 搜索、列表、内容身份查询 | 服务 | 只读逻辑视图 |
| 回收站删除（仅本机文件，可选功能） | 客户端 | 服务只消费 `/verified-changes` 已验证结果 |

理由：

1. 生命周期解耦（§2.1 目标 3）要求构建不依赖 GUI 存活，只有服务内构建能满足。
2. 单一构建者天然满足"单数据库写入器"，多客户端并发下无写冲突，杜绝两处构建造成的索引漂移。
3. 模型运行时只需部署一份（服务侧）；GUI 在 service 模式无需携带视觉运行时，安装包与内存显著缩减。
4. 照片通常在网络位置（WebDAV/NAS），服务贴近数据读取；GUI 中转构建会让照片字节在网络上跑两趟。
5. 反向方案（GUI 构建、服务仅存储）被否决：GUI 关闭即停止构建、多客户端写冲突、每个客户端都要装模型栈、且与"服务是只读逻辑视图"的定位冲突。

#### 来源访问与授权委派（统一原则，本地与网络照片一致）

**客户端只做登记与授权，永远不做数据中转；服务直接读取来源并构建，无论照片在本地磁盘还是网络（WebDAV/NAS）。** 客户端是"授权桥梁"——把来源和凭据委派给服务——而不是"数据桥梁"。本地照片不采用"GUI 本地构建后把结果放入服务"的模式：那会重新引入双构建者、写冲突、GUI 必须常驻、模型栈重复等已被否决的问题（见上），且与网络来源形成两套构建路径。

来源登记与凭据委派流程：

1. **本地路径来源**：客户端提交路径，服务校验可读性（枚举 + 读取探测）；服务不可读的路径拒绝登记。不支持"GUI 可见但服务不可见"的来源，也不提供客户端中转兜底。
2. **WebDAV 来源**：客户端经由已认证通道（管道/HTTPS）把来源地址与凭据**一次性**交付服务；服务存入自己的 Windows 凭据管理器/DPAPI（复用 `WindowsCredentialStore` 模式，该组件需从 `Picer.App.Security` 下沉共享），不落日志、不落明文配置。
3. **授权可撤销**：删除来源即销毁服务侧凭据并停止该来源的全部访问；索引数据按用户选择保留或清除，原始照片不受影响。
4. **审计**：登记、凭据写入、撤销均写结构化审计日志（不含凭据值）。
5. **隐私视角**：来源列表即授权清单——用户对服务能读什么有完全可见性；服务读不到的内容永远不会进索引。

### 4.2 新项目

```text
server/Picer.IndexService/
  Program.cs                 // 监听器装配：Kestrel(HTTPS) 与 NamedPipe 帧宿主，二选一或并存
  Api/                       // 传输无关的端点处理器（DTO 进/出），三种传输共享
  Auth/TokenStore.cs         // 首次启动生成 bearer token，写本地受限文件 + 可导出配对
  Jobs/JobManager.cs         // 扫描/哈希/识别长任务：进度、取消、断点续作（复用现有引擎语义）
  Endpoints/                 // Images, Search, Sources, Thumbs, Duplicates, People, VerifiedChanges, Jobs
```

- `net8.0`、x64（与全仓一致，OpenCV/DirectML 原生依赖要求）。
- 数据根目录解析顺序：`PICER_INDEX_DATA_ROOT` 环境变量 → 服务自身 `data-location.json`（与 App 的 `AppPaths` 同构，支持相对路径，基准为服务 exe 目录）→ 默认 `%LOCALAPPDATA%\Picer\service-data`。
- 服务启动时执行现有 `PicerDatabase` 迁移链；迁移前整库备份要求不变（db+WAL+SHM，服务关闭状态由服务自身保证：启动迁移期间拒绝 API 请求）。

### 4.3 并发与事务

- 沿用"单数据库写入器"：服务内部所有写操作经单一写入队列串行提交（现有 100 条短事务语义不变）。
- 读连接使用 WAL 并发读；列表查询仍为 keyset 分页，`totalCount/loadedCount/continuation` 语义原样透传到 JSON。
- 多 GUI 客户端并发安全：所有写都收敛到服务，客户端之间无共享文件，不存在双写。

### 4.4 后台任务

`JobManager` 把现有"分析通道容量 4、读取/解码 1–4 工作器、单写入器"的索引流水线原样移入服务进程：

- `POST /scan` 返回 `jobId`；`GET /jobs/{id}` 轮询阶段化进度（enumerate/hash/metadata/decode/thumbnail/recognition/database，与现有 EventSource 阶段命名一致）；`POST /jobs/{id}/cancel` 取消（取消 <2s 验收标准不变）。
- 缺失对账规则原样保留：仅当来源枚举完整成功才按 `last_seen_scan_id` 对账；服务端 WebDAV 错误向上传播并阻止对账。

### 4.5 内容身份与跨源重关联（哈希为锚点）

#### 问题

现状 `images` 是"路径记录"（path/size/mtime/sha256 同表，SDD §4.1），派生表（`face_instances`、`visual_predictions`、`analysis_state`）外键指向 `image_id`。同内容照片换路径或换来源重新导入会被当成全新照片：重新哈希、重新推理、人物/标签/相册关联全部丢失。这与"哈希是身份锚点"的需求冲突。

#### 数据模型演进（schema v30，已于 2026-09-08 实施）

拆分为内容层与物理层（已落地，见实施报告 §P2.1）：

```sql
-- 内容层：内容的稳定事实，键为 SHA-256，换路径不变
CREATE TABLE image_contents (
    sha256 TEXT PRIMARY KEY,
    file_size INTEGER NOT NULL,
    ahash INTEGER, dhash INTEGER,
    content_hash_state TEXT NOT NULL DEFAULT 'complete',
    derivative_version INTEGER NOT NULL DEFAULT 1
);

-- 物理层：路径实例；一张内容可同时存在于多个路径
-- images 保留并新增 content_sha256 TEXT（引用完整性由应用层保证，与 sha256 列同风格）
-- v30 迁移含既有数据回填：INSERT OR IGNORE image_contents ... SELECT ... FROM images WHERE sha256 IS NOT NULL
```

实现说明：派生表（face_instances 等）暂保持 `image_id` 外键不动——重关联算法优先"移动"语义（更新原记录路径、保留 record id），派生数据经 id 天然延续；仅在"复制出第二份"时新建实例、内容层共享。文本投影键迁移与派生表挂内容层留待 P2 后续增量。

- **内容稳定事实**（换路径不变）：SHA-256、文件大小、感知哈希、人脸向量与实例、视觉预测、描述/摘要、缩略图、**用户确认的人物归属、用户标签、相册成员关系**。人工确认属于内容，不属于路径。
- **物理事实**（换路径即重建）：路径、来源、修改时间、扫描可见性（active/missing）、`last_seen_scan_id`。
- **文本投影键**：媒体文字索引（`MediaTextIndexRepository` 投影表与 FTS）迁移后键挂内容层（sha256），浏览/筛选经物理层 join 解析路径；`tokenizer_version` 与增量更新语义不变。

#### 扫描期重关联算法

对每个新发现路径（新路径或 `missing` 记录复活）：

1. 计算 SHA-256（沿用流水线后台哈希阶段；无变化文件跳过逻辑不变）。
2. 查 `image_contents`：
   - **唯一命中**：挂接内容、仅重建物理元数据，派生数据与用户确认的关联原样继承，**零模型推理**。优先视为"移动"：若库内已有该内容的 `missing` 记录且其旧路径已不存在，原地更新该记录的路径（保留 record id 与全部关联）；若旧路径仍存在（内容被复制出第二份），新建物理实例，内容层共享。
   - **多命中冲突**（同一哈希对应多条 `missing` 记录）：确定性规则择优（同来源优先、原目录树最近祖先优先）；无法唯一判定进入待审阅队列，不静默合并。
   - **未命中**：新建内容层记录，进入正常分析流水线。
3. 哈希失败（读取错误/文件竞态）：沿用 `content_hash_state=failed` 可重试语义，**不做任何关联决策**。
4. 全部重关联动作写结构化日志（image id、sha256 前缀、动作、原/新路径），可审计；物理层挂接是可逆更新，不删除任何数据。

#### 与既有机制的关系

- `MISSING-001` 手动批量重定位保留，作为自动重关联无法唯一判定时的兜底；其 SHA-256 校验规则不变。
- `deleted_utc` 语义不变：仅记录经验证的用户操作；扫描对账只标 `missing`，**不删除内容层**——照片"离开又回来"时全部关联自动恢复，这正是本节目标。
- 感知哈希（aHash/dHash）仍只用于相似分组与连拍，**不参与身份判定**；身份只认 SHA-256。
- 内容身份层对单体模式同样有价值：该迁移可独立于服务化先行实施（见 §12 开放问题）。

## 5. 通信协议

### 5.1 传输与认证（按部署形态选择）

**决策：协议跟随部署形态，同机双进程默认非网络协议（命名管道），跨机才使用 HTTPS；单进程合一无协议（直调）。逻辑与 API 三种形态完全共享。**

| 部署形态 | 传输 | 安全 | 说明 |
|---|---|---|---|
| 单进程合一（`local` 模式，默认回归基线） | 无协议：`LocalGateway` 进程内直调 Core | 进程边界即 OS 用户边界 | 现状行为，契约测试的第三条腿 |
| 同机双进程（服务+GUI，**合一部署默认形态**） | **命名管道**（非网络协议）：`NamedPipeServerStream` + 长度前缀 JSON 帧，复用与 HTTPS 相同的端点处理器 | 管道 ACL 限当前用户 SID + bearer token 双重防线；服务启动时以随机后缀创建管道名，token 以 DPAPI（CurrentUser）加密后与管道名一并写入 `%LOCALAPPDATA%\Picer\bootstrap\service-endpoint.json`（受限 ACL）供 GUI 自动发现 | 免证书、免端口、免防火墙面；GUI 关闭后服务继续构建（这是合一部署选双进程而非单进程的理由） |
| 跨机分离（显式 opt-in） | HTTPS 强制 | 自签证书 + TOFU SPKI 固定 + bearer token；默认端口 `https://<host>:8445`（避开 ModelServer 8090/8443）；证书轮换需两端人工确认；**配对 UI 必须显示证书 SPKI 指纹供人工核对，防首次连接被同主机恶意服务抢占** | 客户端配置指向非回环地址时，UI 必须单独展示目标主机、数据类别并二次确认（对齐 SDD §7"外发前独立授权"） |

> 威胁模型声明：同 Windows 用户的本地进程视为可信边界——管道帧不加密，token 与管道 ACL 是纵深防御而非安全边界；跨机通信一律 HTTPS，无明文选项。

认证统一：三种传输都要求 bearer token（复用 `PICER_MODEL_SERVER_API_KEY` 的常量时间比较模式）；同机场景 token 随 `service-endpoint.json` 自动配对，跨机场景由用户手动配对；token 存 Windows 凭据管理器（新增 `PicerCredentialTargets.IndexService`），不落日志、不落普通配置。

审计（服务端）：记录调用方、传输、端点、耗时、状态码、图像 ID 数量；不记录路径全文、人物名、查询全文（SDD §11.7 日志约束同样适用于服务侧）。

> 为什么同机不默认 HTTPS loopback：TLS 证书生成/固定/轮换对桌面用户是持续运维负担，而性能不是理由——三者都在 §8 预算内。反过来，跨机不提供非加密通道：局域网明文索引元数据（含人物、地名）不可接受。

### 5.2 API 草案（`/api/gateway/v1`）

```text
GET  /health                                  → { status, schemaVersion, dataRoot }
GET  /sources                                 → 来源列表（即服务当前被授权可读的来源清单）
POST /sources                                 → 登记来源并委派凭据：服务先做可读性校验，失败即拒绝；WebDAV 凭据经认证通道一次性交付，服务侧入凭据管理器
DELETE /sources/{id}                          → 撤销来源授权：销毁服务侧凭据、停止访问；索引数据去留由用户决定
POST /scan          { sourceId }              → 202 { jobId }
GET  /jobs/{id}                               → { stage, processed, total, cancelable }
POST /jobs/{id}/cancel

GET  /images?sourceId&status&cursor&limit=200 → { items[], totalCount, loadedCount, continuation }
GET  /images/{id}                             → 单图完整记录（元数据、标签、人脸汇总）
GET  /thumbs/{imageId}/{derivativeVersion}    → 缩略图字节；ETag=缓存键哈希，支持 304
POST /search                                  → 结构化筛选 + FTS 文本，返回同 /images 形状 + 命中依据
GET  /contents/{sha256}                       → 内容身份查询：该内容已有关联（人物/标签/相册/派生状态），用于导入预览
POST /duplicates/candidates                   → 仅候选与建议（状态机 Detected，永不含执行指令）
GET  /duplicates/plans/{planId}               → 确认快照回读
POST /verified-changes { changes[] }          → 客户端上报**已验证的外部事实**（本机回收站成功项、用户确认的处置）；服务仅据此更新索引记录，绝不触碰文件
POST /tags  /people  /albums …                → 用户标签、人物、相册写操作（参数化 SQL 留在服务端）
```

设计约束：

1. **服务端没有任何端点会修改、移动、删除原始文件**；服务是对照片的只读逻辑视图。索引记录的增删改只由两类输入驱动：扫描对账发现的客观事实（文件出现/消失/哈希命中，§4.5）与客户端上报的已验证事实。
2. 用户可见的删除能力只存在于"文件本机"的 GUI，走现有 `IUserAuthorizedFileOperation` 回收站流程与破坏性状态机；服务不参与执行，只消费已验证结果（见 §7）。
3. 禁止随意放宽：分页 `limit` 服务端钳制（≤500）；不支持的服务器端筛选返回明确的 `unsupported` 字段，不伪装零结果。
4. 所有请求体尺寸上限（默认 4 MB）、并发连接数限制、每客户端速率限制（防失控轮询）。
5. 多客户端并发写逻辑数据（标签/人物/相册）：服务端单写入器顺序化，等效字段级 last-writer-wins；写成功后经既有领域事件通知其他客户端刷新，不做交互式合并。

### 5.3 缩略图与缓存

- 客户端磁盘缓存复用现有版本化缓存布局（图片身份+尺寸+`derivative_version` 键、临时文件原子替换），仅把"来源"从磁盘/WebDAV 换成 HTTPS。
- ETag 命中返回 304；客户端 single-flight 与容量 LRU 语义不变（PERF-004 的验收在 HTTP 模式下重跑）。

## 6. 客户端改造

### 6.1 `IIndexGateway` 双实现

```csharp
public interface IIndexGateway
{
    IAsyncEnumerable<PhotoPage> ListAsync(PhotoQuery query, CancellationToken ct);
    Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct);
    Task<Stream> OpenThumbnailAsync(long imageId, string derivativeVersion, CancellationToken ct);
    Task<JobHandle> StartScanAsync(long sourceId, CancellationToken ct);
    Task ReportVerifiedChangesAsync(VerifiedChangeReport report, CancellationToken ct);
    // …与 AppServices 现有调用面一一对应
}
```

三个实现对应三种部署形态，跑同一套契约测试（见 §10）：

- `LocalGateway`：进程内直调 `IndexEngine/CompositeSearchEngine/…`，`local` 单进程形态默认，桌面 E2E 回归基线。
- `PipeGateway`：同机双进程默认传输；命名管道帧 ↔ 同一端点处理器。
- `HttpGateway`：跨机 HTTPS（`Picer.Core` 新增 `Gateway/HttpIndexGateway.cs`），超时/重试/取消语义与现有搜索 debounce 一致：新查询取消旧请求。

### 6.2 客户端配置

`%LOCALAPPDATA%\Picer\bootstrap\backend.json`（相对路径以 App exe 目录为基准，规则与 `data-location.json` 一致）：

```json
{ "mode": "local", "transport": "pipe", "baseUrl": "https://nas:8445", "thumbCacheMiB": 4096 }
```

- `mode: "local"`（默认）：一切如旧，无网络栈。
- `mode: "service"`：同机时 `transport: "pipe"`（自动从 `service-endpoint.json` 发现管道名与 token）；`transport: "https"` 用于跨机，需手动配对并二次确认目标主机。
- 设置窗口"索引服务"页：显示当前形态、连接/证书状态、token 配对入口、延迟与版本探针（复用 `ModelServiceProbe` 模式）。
- 数据迁移：从 `local` 切到 `service` 时，引导用户走既有"迁移数据目录"流程（`ScheduleMigration` 模式）把数据根交给服务，禁止两份索引并存双写。

### 6.3 离线与降级

- 服务本机但未启动：主窗口显示"索引服务未运行：[启动服务] [重试] [使用本地模式]"；不静默失败。
- 远程服务网络分区：进入只读降级，标题栏与网格持续显示"离线缓存"标识，仅展示最近缓存页；所有写操作禁用并说明原因（对齐 server/README 的离线标注要求）。
- 服务升级/迁移期间：`/health` 返回维护态，客户端轮询等待，不报错。

## 7. 只读边界与删除能力的归属

服务是只读逻辑视图，**删除不是服务的职责**；构建、删除执行、文件修改都不发生在服务（§4.1）。

| 场景 | 行为 |
|---|---|
| 照片在 NAS/WebDAV（第三方机器） | 只读索引 + 建议，无删除执行路径（回收站是本机概念）；用户在 NAS 侧删除后，扫描对账将该路径标为 `missing`，内容层与全部关联保留——照片重新出现即自动恢复（§4.5） |
| 本机 GUI 删除本机文件（可选功能） | 沿用现状 `WindowsRecycleBinFileOperation` + 破坏性状态机（DUP-002/DELETE-001 已验收）；执行成功后客户端调 `/verified-changes` 上报，服务删除对应物理记录、**保留内容层**（哈希仍可重关联） |
| 服务端删除/修改文件 | 明确不做：服务部署只需照片路径的读权限 |
| 裸 SMB/UNC 共享来源 | 只读索引可用；无删除能力 |

破坏性状态机 `Detected -> Reviewed -> Confirmed -> Executing -> Verified -> Completed/PartiallyFailed` 仅存在于本机 GUI 删除场景；服务侧没有任何状态可以触达原始文件。

## 8. 性能与容量预算

| 指标 | 预算 | 依据 |
|---|---|---|
| 首屏 200 张列表（含元数据） | service 模式 p95 < 250 ms（HTTPS loopback 基准；管道/直调只快不慢） | 现有 SQL 预算 150 ms + 序列化/传输余量 |
| 结构化+FTS 组合搜索 | service 模式 p95 < 300 ms | SDD §11.9 复合查询预算 + HTTP 余量 |
| 传输开销（同 size 载荷，loopback） | 管道与 HTTPS keep-alive 稳态 RTT 均 < 1 ms；契约测试断言三传输行为一致而非性能差异 | §5.1 协议选择依据 |
| 缩略图命中客户端缓存 | 0 网络；未命中单张 < 100 ms (loopback) | PERF-004 |
| 扫描吞吐 | 与单体模式差距 < 10% | 服务内流水线与现共享 Core 实现 |
| 哈希命中重关联（§4.5） | 单张 p95 < 50 ms（纯索引操作）；断言零哈希重算、零模型推理 | 内容身份层设计 |
| GUI 内存 | service 模式下客户端不再加载 SQLite/引擎/模型，预期下降 | 以现有工作集基线复测 |

10 万图库基准（PERF-001 工具）需在 `local` 与 `service` 两种模式各出一份对照报告后才能宣布达标。

## 9. 安全与隐私（对 SDD §7/§11.7 的映射）

1. SQL 参数化全部保留在服务端；HTTP 层不拼 SQL 字符串。
2. token、凭据不写日志（服务端与客户端同样约束）；异常信息脱敏后返回。
3. 非 loopback = 独立授权点：首次配置、每次目标主机变更都需确认；UI 显示数据类别与数量。
4. 人脸向量、人物名不出现在 URL、查询串、日志；列表接口对候选人物默认脱敏（仅返回 `personId`，名称按现有 People 授权规则下发）。
5. 服务暴露面最小化：仅绑定配置地址（默认仅 loopback 管道/端口）；不提供文件系统任意路径读写端点；`/sources` 接受的路径记录与现有"来源登记"一致，不构成任意读。
6. 服务侧凭据仅存 Windows 凭据管理器/DPAPI；客户端经认证通道一次性委派（§4.1），撤销即销毁；日志与审计不含凭据值。

## 10. 测试与验收

1. **契约测试（关键）**：同一套测试套件跑全部三种传输（`LocalGateway` 直调、`PipeGateway` 真实命名管道、`HttpGateway` 真实 Kestrel loopback + 自签证书），断言行为一致：分页语义、取消、冲突、错误码、缓存头。防"多套实现漂移"。
2. 服务端集成：迁移（启动期拒绝请求）、单写入器串行、多客户端并发读、任务取消 <2s、`/verified-changes` 幂等（幂等键去重，重复上报不产生二次索引更新）。
3. 破坏性回归：SDD §9.1 全套（确认/取消/竞态/哈希变化/回收失败/部分成功）在 service 模式重跑；未确认时零文件操作。
4. 重关联回归（§4.5）：移动/改名后扫描自动恢复关联且零重推理（以模型调用计数断言）；复制双路径共享内容层；同哈希多 `missing` 冲突进入待审阅；删除后重导入恢复人物归属与用户标签；哈希失败不产生任何关联决策。
5. 韧性/混沌：扫描中途杀死服务进程后重启 → WAL 恢复一致、任务可断点续作；网络分区进入降级并可恢复；磁盘满/凭据失效返回显式错误。
6. 隐私：代理断言非本机通道无图像、人脸向量、人物名、查询全文外发；服务日志不含敏感字段。
7. 性能：§8 预算双模式对照；PERF-001 JSON 报告纳入两种模式。
8. E2E：现有桌面自动化默认继续跑 `local`；新增 service 模式冒烟（测试脚手架管理服务进程生命周期：启动/等待健康/清理）。

## 11. 分阶段实施计划（获批后执行）

| 阶段 | 内容 | 规模 | 退出条件 |
|---|---|---|---|
| P1 只读服务化 | `Picer.IndexService` 骨架、传输无关端点处理器、Kestrel HTTPS、`HttpGateway` 与契约测试；服务模式交付为**只读**（服务读自己的数据副本或已迁移库，不提供混合读写——避免同一图库双 owner） | L | 契约测试三传输全绿；10 万列表/搜索达 §8 预算 |
| P2 内容身份、管道与写路径 | schema v30 内容层迁移（分批短事务、可断点、失败整体还原备份，附迁移/回滚实施报告）、扫描期哈希重关联、`PipeGateway` 成为同机默认、来源登记/凭据委派、扫描任务、标签/人物/相册写、`/verified-changes`；客户端全量切 service 模式可用 | L | §10.3–§10.5 通过；GUI 关闭后扫描持续推进可验证 |
| P3 运维与远端 | 远端 opt-in 授权流、证书轮换、备份/恢复 API 化、诊断中心接入服务探针、性能双模式报告、文档与 AGENTS.md 更新 | M | RELEASE_READINESS 增补条目；E2E 冒烟入 CI（loopback 管道） |

每阶段按仓库惯例出具带日期的实施报告；schema v30 走 `user_version` 迁移链并附迁移/回滚方案（P2 交付）。

## 12. 风险与开放问题

1. **多实现漂移**：`IIndexGateway` 三个实现长期并存有维护成本。缓解：契约测试强制三种传输行为一致；P3 后评估形态收敛（例如同机统一双进程，缩减 local 单进程形态）。
2. **自启动与提权**：服务作为用户进程（托盘/开机启动）先交付；Windows 服务（Session 0）形式待定，涉及回收站会话边界。
3. **与 ModelServer 的关系**：是否最终合并为一个 `Picer.Server`？本期保持两个进程（部署目标不同：索引服务贴着数据与用户，模型服务贴着 GPU），仅在鉴权与探针工具上复用。
4. **开放问题（需用户决策）**：
   - 默认端口 8445 是否合适？
   - 远端（LAN 内其他机器）模式是否属于首批需求，还是先只做同机分离？
   - `local` 模式最终是否保留为永久选项（影响 P3 的交付范围）？
   - 内容身份层迁移（v30）是否先于服务化在单体模式单独实施？单体用户同样受"移动后重新导入丢关联"影响，提前迁移可先兑现哈希重关联价值。
   - 安装包形态：单包双形态，还是 GUI 精简包（service 模式免模型栈）+ 服务完整包？

## 13. 现有代码影响面分析（基于当前代码逐文件核对）

### 13.1 总览：新增 / 修改 / 不变

**新增（N）**

| # | 位置 | 内容 | 阶段 |
|---|---|---|---|
| N1 | `server/Picer.IndexService/`（新项目 8–10 文件） | `Program.cs`（Kestrel+管道监听装配）、`Api/IndexApiHandlers.cs`（传输无关处理器）、`Api/Dtos.cs`、`Auth/TokenStore.cs`（DPAPI）、`Jobs/JobManager.cs`、`Endpoints/*` | P1/P2 |
| N2 | `src/Picer.Core/Gateway/IIndexGateway.cs` | 网关接口 + DTO；**直接复用**现有 `SearchCursor/SearchPage`（`IndexSearchEngine.cs`）、`ImageRecord`、`SearchFilters` 类型，不另造分页语义 | P1 |
| N3 | `src/Picer.Core/Gateway/HttpIndexGateway.cs` | HTTPS 客户端 | P1 |
| N4 | `src/Picer.Core/Gateway/PipeIndexGateway.cs` | 命名管道帧客户端 + `NamedPipeServerStream` 帧宿主 | P2（P1 仅 HTTPS） |
| N5 | `tests/Picer.Tests/Gateway/ContractTests.cs` | 三传输契约测试 | P1 |
| N6 | `tests/Picer.E2E/ServiceModeSmokeTests.cs` | 服务进程生命周期脚手架（启动/等健康/清理）+ 冒烟 | P2 |

**修改（M）——按工作量降序**

| # | 文件 | 现状（已核对） | 变更 | 阶段 |
|---|---|---|---|---|
| M1 | `src/Picer.App/ViewModels/MainViewModel.cs` | **33 处**直接引用 `AppServices.*`，全仓最大消费面 | 读路径迁移到 gateway；删除执行点（`MoveToRecycleBinAsync` 调用处）追加 `/verified-changes` 上报 | P1 读 / P2 写 |
| M2 | `src/Picer.App/AppServices.cs`（266 行静态服务定位器） | 进程内组装 Db/Index/缩略图/视觉/转写全部栈；`Reinitialize/ApplyStorageConfig/CreateCloudVisionService` 等工厂 | `Initialize()` 按 `backend.json` 分支：local 分支保留现状组合；service 分支**不创建** Db/Index/Thumbnails/视觉栈，只建 gateway；工厂类（云端视觉等）保留客户端侧 | P1 |
| M3 | `src/Picer.Core/Data/PicerDatabase.cs` | `CurrentSchemaVersion = 29`；`UpsertImage/GetImages/MarkMissing/DeleteImage` 等按路径记录操作 | v30 迁移：新增 `image_contents`、派生表外键改挂内容层（分批短事务）；数据访问层新增内容层 API（`GetOrCreateContent` 等）；现有查询加内容 join | P2 |
| M4 | `src/Picer.Core/Indexing/IndexEngine.cs` | `IndexSourceAsync/VerifyAsync/RemoveIndexRecordAsync/RelocateAsync/BuildRelocationPlanAsync/ApplyRelocationPlanAsync`；重定位已按 SHA-256 校验 | `IndexSourceAsync` 扫描期哈希重关联（§4.5 算法）；`RemoveIndexRecordAsync` 拆分"删物理记录/保留内容层"；重定位计划可借内容层去重 | P2 |
| M5 | `src/Picer.Core/Thumbnails/ThumbnailService.cs` | 缓存键 = `imageId`（`GetCachePath(long imageId)`） | 缓存键改 `sha256`（内容层共享）；`Delete(imageId)` 语义随物理记录调整；旧缓存按需重建 | P2 |
| M6 | `src/Picer.Core/Vision/VisionAnalysisRepository.cs` + People 相关仓储 | `face_instances/visual_predictions/analysis_state` 外键 `image_id` | SQL 改挂内容层（迁移配套） | P2 |
| M7 | `src/Picer.Core/Search/MediaTextIndexRepository.cs`、`IndexSearchEngine.cs`、`DuplicateDetector.cs` | 文本投影/搜索按 image_id；重复分组按内容哈希字段在 images 表 | 投影键对齐内容层；`DuplicateDetector` 分组天然跨路径合并 | P2 |
| M8 | `src/Picer.App/AppPaths.cs` | bootstrap 只有 `data-location.json` | 新增 `backend.json`/`service-endpoint.json` 读取与相对路径解析 | P1 |
| M9 | `src/Picer.App/Security/WindowsCredentialStore.cs` | 位于 App 工程，服务无法复用 | 下沉 `src/Picer.Core/Security/`，App 改引用 | P2 |
| M10 | `src/Picer.App/ViewModels/` 其余 + 窗口（`MainWindow.xaml.cs` 8 处、`StorageSettingsWindow` 6 处、`VideoViewModel` 2 处、`CloudVisionTaskCenterWindow` 2 处等） | 零散 `AppServices.*` 引用 | 随 M2 适配层自动兼容，逐步直连 | P1/P2 |
| M11 | `src/Picer.App/DataProtectionService.cs` | 备份/恢复/迁移全部假设 GUI 本地数据根 | service 模式守卫（local-only）→ P3 API 化 | P3 |
| M12 | `src/Picer.App/DiagnosticService.cs` + `Views/DiagnosticCenterWindow` | 探针仅覆盖 ModelServer/TTS/ASR 端点 | 接入索引服务探针（复用 `ModelServiceProbe` 模式）+ 管道/HTTPS 状态显示 | P3 |
| M13 | `App.xaml.cs`（`OnStartup` 第 28 行起） | 启动序：pending migration/restore → AppServices | `local` 不变；service 模式跳过本地迁移类启动项，增加服务连接等待/降级分支（§6.3） | P1 |
| M14 | `picer.sln`、`tools/publish-self-contained.ps1` | 单产物 | 注册新项目；双产物发布（GUI 精简/服务完整，§12 开放问题） | P1 注册 / P3 发布 |

**不变（复用原样）**

- `src/Picer.Core/Storage/`（`LocalStorageProvider/WebDAVStorageProvider/CachedStorageProvider`）——服务直接复用，REMOTE-001 语义原样生效。
- `src/Picer.Core/Vision/`（36 个文件：YuNet/SFace/CLIP/Florence/转写）——全部在服务进程内复用，不迁移不改写。
- `src/Picer.Core/FileOperations/` + `src/Picer.App/FileOperations/`——回收站执行留在客户端。
- `src/Picer.Core/Diagnostics/ModelServiceProbe.cs`、`EndpointBenchmarkService.cs`——探针模式复用。
- `server/Picer.ModelServer/`——不动，独立演进。

### 13.2 最大改造风险：静态服务定位器消费面

`AppServices.*` 在 10 个文件被引用 60+ 处（`MainViewModel` 占 33）。两个策略：

- **策略 A（P1 采用）**：保持 `AppServices` 静态面不变，把其属性实现改为"gateway 优先、local 直调"的适配层——ViewModel 几乎零改动，风险最低。
- **策略 B（P2 渐进）**：ViewModel 构造函数注入 `IIndexGateway`，逐步删除静态依赖；每迁移一个 ViewModel 跑对应 E2E。

### 13.3 与实施计划的对应

M1–M5、M8、M13 → P1/P2 主线；M6/M7/M9 → P2 内容层配套；M11/M12/M14 发布与运维 → P3。每项改动落地时更新本节勾选状态。
## 2026-09-20 实施状态

HTTPS IndexService、SPKI/令牌契约、客户端重试和验收脚本已实现；跨机器证书信任、断线恢复和远程图库联调仍属于实机验收，不应标记为全部完成。
