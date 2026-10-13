# Picer 单租户远程 GUI / IndexService / MediaAgent SDD

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
> 后续替代：IndexService 已提供独立 GUI/Agent、授权同步、分析、修复和索引清除接口；“仅只读网关”“远程人工修改不回传”“等待批准”的表述均为本阶段背景。原始媒体仍只读，索引清除不等于删除原文件，远端能力仍受配置及授权约束。
<!-- /historical-status-2026-10-10 -->

日期：2026-09-20  
状态：设计初稿，待领域专家评审  
范围：同一 Picer GUI 在本地模式和远程 IndexService 模式之间切换；原图保留在图库所在计算机，通过本地 MediaAgent 按需提供给远程 GUI。

## 1. 目标与非目标

### 1.1 目标

1. 本地和远程使用同一个 `Picer.App` 程序及同一套主要 UI。
2. 本地 GUI 扫描原图、建立索引、生成缩略图，并将逻辑索引增量同步到 Linux IndexService。
3. 任意远程计算机通过菜单选择“连接远程 IndexService”即可浏览同一单租户图库。
4. 远程 GUI 优先读取本地缓存；缓存未命中时通过 IndexService 请求原图，由图库所在计算机的 MediaAgent 返回。
5. 原图默认不上传到 IndexService；IndexService 保存元数据和可选的缩略图副本。
6. 所有数据传输可取消、可重试、可断点续传，并通过内容哈希校验。

### 1.2 非目标

- 本阶段不实现多租户、用户账户、角色权限和共享图库。
- 不把远程 IndexService 当作原图永久存储。
- 不复制或直接暴露本地 SQLite 文件作为同步协议。
- 不允许远程服务根据客户端提交的绝对路径访问本地文件。

## 2. 运行模式

配置文件只面向用户保存 IP，不保存完整 API URL：

```json
{
  "mode": "local",
  "indexServiceIp": "124.156.193.122"
}
```

程序固定组装：`https://{indexServiceIp}:8445/`，API 路径由客户端常量生成。`mode` 取值：

- `local`：`LocalGateway`，本机 SQLite、缩略图和原图。
- `remote`：`HttpIndexGateway`，远程索引、远程缩略图和 MediaAgent 原图代理。

菜单：

- 索引服务 → 使用本地索引
- 索引服务 → 连接远程 IndexService

切换流程：停止当前页面的加载任务 → 建立新 Gateway → `/health` 探针 → 加载 sources/目录树 → 加载照片页。切换失败时保留原模式和定位，不清空当前 UI。

用户配置不出现密码、Token、端口和 API 路径。Bearer Token、SPKI Pin、设备 ID 存放在当前用户受保护凭据/配置中；首次配对或部署脚本负责写入。

## 3. 组件职责

### 3.1 Picer.App

- 统一 UI、搜索、目录树、人物和标签展示。
- 通过 `IIndexGateway` 读取远程/本地元数据。
- 管理缩略图和原图缓存。
- 不能在 remote 模式访问本地原图绝对路径。
- 需要本地数据库的编辑功能必须明确标记为远程不可用，或改为远程命令 API。

### 3.2 IndexService（Linux）

- 保存单租户逻辑索引、元数据、缩略图缓存和同步游标。
- 提供查询、搜索、增量同步、缩略图读取、原图代理会话 API。
- 不保存图库计算机的绝对路径，只保存 source UUID、相对路径和内容哈希。
- 记录 Agent 在线状态、能力、最后同步游标和请求审计。

### 3.3 Picer.MediaAgent（图库所在计算机）

- Windows 常驻进程/服务，由本地 GUI 安装和启动。
- 主动向 IndexService 建立 HTTPS 长连接（优先 WebSocket，备用长轮询），不要求公网入站端口。
- 维护 source UUID 到本地目录的映射。
- 只接受由 IndexService 转发且经过鉴权的 content hash 请求。
- 读取前重新验证文件存在、大小、mtime 和 SHA-256；不一致则返回 `asset_changed`。
- 支持缩略图补传和原图 Range 分段传输。

## 4. 索引同步协议

同步对象使用逻辑 ID，不直接复制 SQLite：

```text
tenantId（单租户固定）
sourceId（稳定 UUID）
relativePath
contentSha256
size
mtimeUtc
mediaType
metadataVersion
recordVersion
thumbSha256
deleted
```

接口草案：

```text
POST /api/sync/v1/sources/register
POST /api/sync/v1/changes/push
GET  /api/sync/v1/changes?cursor=...
POST /api/sync/v1/sync/ack
GET  /api/sync/v1/sources
```

要求：

- 以 `recordId + recordVersion + batchId` 做同步幂等键；`sourceId/relativePath/contentSha256` 只用于内容和路径一致性校验；
- 每个 source 独立 cursor；
- push 可重复提交，不产生重复记录；
- 删除采用 tombstone，保留一段时间防止旧客户端复活记录；
- 同一原图、连拍组、视频关键帧保留关联组标识；
- 索引同步不传原图二进制；缩略图可按策略推送。

## 5. 缩略图和原图协议

### 5.1 缩略图

现有 GUI 图片网格显示的是缩略图/预览图，不是原图。缩略图由 `ThumbnailService` 生成并缓存，数据库仅保存缩略图路径/状态。远程模式下缩略图应以内容哈希缓存为主：

```text
GET /api/assets/v1/thumbnails/{thumbSha256}
ETag: "{thumbSha256}"
```

如果 IndexService 没有缩略图，服务端向 MediaAgent 请求后缓存，再返回给 GUI。

### 5.2 原图

```text
POST /api/assets/v1/sessions
GET  /api/assets/v1/{contentSha256}
Range: bytes=start-end
If-None-Match: "contentSha256"
```

流程：

1. GUI 根据索引中的 `contentSha256` 请求资源会话；
2. IndexService 校验远程 GUI 权限和 Agent 在线状态；
3. IndexService 向对应 MediaAgent 发起请求；
4. MediaAgent 本地校验文件哈希后按 Range 返回；
5. IndexService 流式转发，不落盘或按租户策略短暂缓存；
6. GUI 写入临时缓存，校验完整 SHA-256 后原子改名；
7. 后续打开直接使用缓存，不重复传输。

错误码：`agent_offline`、`asset_not_found`、`asset_changed`、`hash_mismatch`、`range_invalid`、`access_denied`。

## 6. 缓存策略

远程 GUI 缓存根目录由配置决定，默认放 D 盘：

```text
D:\PicerCache\thumbs\{sha256}
D:\PicerCache\originals\{sha256}
D:\PicerCache\partials\{sha256}.part
```

- LRU + 最大空间上限；
- 临时文件只能在哈希验证成功后转正；
- 同一哈希跨目录、跨客户端复用；
- 缓存命中不访问 Agent；
- 原图缓存清理不影响索引和原文件。

## 7. 安全与隐私

- HTTPS 强制；客户端校验 SPKI Pin；
- Token 不进入用户可编辑的 IP 配置文件；
- Agent 使用设备证书/短期令牌；
- 所有资产请求按 content hash 授权，不接受任意路径；
- 日志不得记录 Token、原图内容、人脸向量和完整原图路径；
- 单租户不等于匿名访问，部署仍必须有设备级鉴权；
- Agent 断开时 IndexService 不缓存失败的半文件。

## 8. 远程 UI 能力边界

首期 remote 模式支持：

- 目录树、时间线、搜索、缩略图、照片信息、原图查看、视频首帧和缓存；
- 只读人物/标签/场景查询。

需要远程命令 API 后才能开放：

- 扫描新目录；
- 编辑标签、地点、人物归类；
- 删除/回收原文件；
- AI 分析和训练提交。

所有远程写操作必须携带 `recordVersion`，服务端冲突时拒绝覆盖并要求刷新。

## 9. 可观测性

记录以下非敏感指标：

- sync cursor、push/ack 数量、失败原因；
- Agent 在线/离线和最后心跳；
- 缩略图命中率、原图缓存命中率；
- 原图传输字节数、P50/P95 延迟、断点续传次数；
- 哈希校验失败、文件变化和权限拒绝数量；
- GUI 当前 Gateway 模式和 IndexService 健康状态。

## 10. 验收标准

1. 本地 GUI 添加目录并完成索引后，远程 IndexService 可查询相同数量的逻辑记录。
2. 同步重复执行不产生重复记录，断线后从 cursor 继续。
3. 远程 GUI 通过菜单连接，仅填写 IP 即可进入完整主窗口。
4. 远程 GUI 首次显示缩略图成功，第二次命中本地缓存且不访问 Agent。
5. 远程双击原图成功，Range 断点续传和 SHA-256 校验通过。
6. 删除或修改本地原图后，远程访问返回 `asset_changed`，不会返回错误内容。
7. Agent 停止时远程 UI 显示明确离线状态，索引浏览仍可用。
8. 本地/远程模式切换不改变目录树结构和当前定位。
9. 未授权设备、错误 Token、错误 SPKI、任意路径请求均被拒绝。
10. 原图不需要上传到 IndexService 持久化目录，远程缓存可清理并重新获取。

### 10.1 黑盒 E2E 验收流程

1. 在本地模式 GUI 添加一个测试目录并完成索引，确认本地照片、缩略图和原图均可打开。
2. 由本地 GUI 执行“同步到 IndexService”，等待同步游标完成；记录服务端图片总数、缩略图数量和最后游标。
3. 创建一个全新的、使用独立 `PICER_DATA_ROOT` 的空 GUI 数据目录，不复制本地 SQLite、缩略图或原图缓存。
4. 在空 GUI 菜单中选择“连接远程 IndexService”，配置只填写 `124.156.193.122`，确认进入完整主窗口而非服务状态窗口。
5. 浏览目录树和搜索结果，确认数量、时间、地点和缩略图与本地 GUI 一致；记录第一次缩略图请求及缓存写入。
6. 双击一张照片请求原图，确认首次通过 IndexService → MediaAgent 传输、SHA-256 校验成功并写入远程 GUI 本地缓存。
7. 关闭照片查看器后再次打开同一照片，确认命中本地原图缓存，不产生第二次 Agent 传输。
8. 清理该照片的远程缓存后再次打开，确认能够重新下载且内容哈希一致。
9. 暂停本地 MediaAgent，确认远程 GUI 仍可浏览已同步索引和缩略图，并对原图显示明确的“本地原图代理离线”提示。
10. 恢复 MediaAgent，重试同一照片，确认原图恢复；在传输中取消请求，确认临时文件不进入正式缓存。

## 11. 分阶段实现顺序

1. 配置规范化：IP 配置、URL 组装、菜单切换和统一 MainWindow。
2. IndexService 增量同步协议和 source/record 逻辑模型。
3. 缩略图哈希缓存和远程缩略图补传。
4. MediaAgent 心跳、配对和原图 Range 代理。
5. GUI 原图缓存、断点续传和错误状态。
6. 远程只读回归测试；再逐步开放远程写操作。

## 12. 专家评审修订（2026-09-20）

本节根据架构、安全和客户端体验三方评审结果加入，作为开发前置约束。

### 12.1 GUI 会话代际

GUI 为每次本地/远程 Gateway 切换生成递增 `GatewaySessionId`。所有异步加载、缩略图和原图任务携带该 ID；返回时若 ID 不是当前会话，结果必须丢弃。切换时取消旧任务、释放旧 Gateway/HTTP 连接，并保留失败前的模式、目录定位和页面数据。

远程模式不能继续走当前仅显示 `IndexServiceWindow` 的分支。必须由统一 `MainWindow` 承载远程只读能力；依赖本地 SQLite 的功能通过 `RemoteRepository`/Gateway API 提供，暂未支持的写操作由 capability 显式禁用。

### 12.2 同步事务与版本

每次同步具有 `syncSessionId`、`batchId`、`sourceRevision` 和 `recordVersion`。服务端先写入临时批次，完成校验后在一个事务中提交记录和 server cursor；只有明确 ACK 后客户端才能推进 cursor。重复 batch 必须幂等，服务端重启后可从临时批次恢复或回滚。

逻辑记录使用稳定 `recordId`，路径变更不产生新记录；删除使用 record tombstone；服务端拒绝旧 `recordVersion` 覆盖新版本。提供全量快照/重新同步入口。远程来源注册不再接受路径，只接受 source UUID、显示名称、相对根名称和指纹。

### 12.3 Agent 注册和资源会话

MediaAgent 使用明确消息类型：

```text
agent.register
agent.heartbeat
agent.capabilities
asset.open
asset.chunk
asset.complete
asset.error
asset.cancel
```

每个资源请求携带 `requestId`、`sessionId`、`sourceId`、`recordId`、`contentSha256`、Range、过期时间和 nonce。IndexService 只向已注册且在线的 source 绑定 Agent 发出请求；Agent 不接受 GUI 直接提交的路径或哈希。

资源会话（`AssetSessionId`）短期有效，并绑定 GUI 设备、Agent、source 和允许的 Range。客户端断开会传播取消；Agent 重启后旧会话失效，GUI 可重新申请。GUI 模式切换使用独立的 `GatewaySessionId`，两者不得混用。

首版 MediaAgent 传输固定使用 HTTPS WebSocket：控制消息为 JSON envelope，数据消息为带 `requestId`、`offset`、`length` 和序号的二进制 frame。一个连接可复用多个并发请求，由 Agent 按最大并发数调度；重连后旧资源会话全部失效，只接收新请求。每个 chunk 必须按 session、序号、偏移、长度和过期时间校验，IndexService 向 GUI 传播取消。

### 12.4 路径和 TOCTOU 防护

- 同步路径统一为 `/` 分隔的相对路径；拒绝绝对路径、盘符、UNC、设备路径和 `..`；
- Agent 规范化后验证路径仍位于已注册 source 根目录内；拒绝越出根目录的 junction/reparse point；
- 资源必须已存在于同步索引；
- Agent 打开只读句柄后，在同一句柄上完成校验和传输，避免校验后被替换；
- 客户端完成整文件 SHA-256 校验后才将 `.part` 原子改名。

### 12.5 HTTPS 和配对

IndexService 证书 SAN 必须包含远程 IP 或固定 DNS；客户端同时执行证书链/有效期/主机名校验和 SPKI Pin 校验。自签名部署必须通过证书/CA 配对，禁止无条件 TOFU。

首次使用采用配对码或管理员批准，签发设备凭据和短期 Token；Token 区分 `index.read`、`asset.read`、`agent.connect` 和后续 `index.write` scope，支持过期、轮换和撤销。用户配置仍只显示 IP，凭据放在系统安全存储；Linux 服务端凭据使用 root-only 文件或 systemd credentials，不出现在日志、命令行和普通环境列表。

`/health` 对外只返回最小 liveness；详细版本、Agent 和数据状态放在鉴权诊断接口。

### 12.6 能力和缓存边界

远程服务返回 capability：

```text
browse, search, originalRead, videoRead,
editMetadata, classifyFace, deleteFile, runAnalysis
```

首期只启用 `browse/search/originalRead/videoRead`。不可用功能在 GUI 中禁用并显示原因，不能点击后无响应。

服务端必须强制执行 scope，而不是只依赖 GUI 隐藏按钮：

| 路由/操作 | 必需 scope |
|---|---|
| `/api/gateway/*` 查询和搜索 | `index.read` |
| `/api/assets/*` 缩略图/原图会话 | `asset.read` |
| Agent WebSocket 注册和心跳 | `agent.connect` |
| `/api/sync/*` 索引同步 | `index.sync` |
| 编辑、删除、分析、训练 | 本阶段不签发 |

`AssetSessionId + requestId + nonce` 只能消费一次；IndexService 保存已消费标识，重复、过期、取消、完成或失败后的请求一律拒绝。客户端不能自行延长有效期或扩大允许的 Range。远程模式下 IndexService 绝不调用 `ThumbnailService.GetOrCreate(record.Path, ...)`，也不使用同步数据中的本地绝对路径生成缩略图或原图；缺失缩略图必须通过已授权 Agent 获取后按 `thumbSha256` 缓存。

远程缓存增加当前用户 ACL、临时文件清理、LRU 文件数和字节双上限、磁盘不足保护、一键清理入口。IndexService 的临时资源缓存有明确最大时长和空间上限；缓存目录不记录本地绝对路径。

### 12.7 视频和并发

视频和照片使用同一套远程浏览与资源代理逻辑。远程 GUI 的目录树、时间线、搜索和媒体类型筛选不切换到另一套视频页面；选择“视频”时显示视频记录和首帧，选择“全部”时图片和视频共用同一时间序列布局。

视频原文件、首帧缩略图、关键帧和视频元数据分别使用独立内容哈希和缓存键；视频原文件支持 Range。远程 GUI 首次浏览只请求首帧/关键帧，双击查看或播放时才按需请求视频原文件；下载的分片写入 `.part`，完整文件或可播放缓存段通过哈希/Range 校验后才可使用。视频的转写、人物时间线和摘要作为索引元数据同步，远程首期只读展示。

IndexService/Agent 设置单设备并发数、单 Range 上限、单文件大小、空闲超时、全局带宽和队列限制。多台远程 GUI 可并发读取同一视频，但服务端必须限制重复上游读取和句柄数量。视频资源协议错误应区分 `video_not_found`、`keyframe_unavailable`、`range_invalid`、`asset_changed` 和 `agent_offline`。

### 12.8 补充回归用例

- 同步中断恢复、重复 batch、服务端重启、全量重同步；
- 本地/远程快速切换时旧请求不得覆盖新页面；
- 两台远程 GUI 并发读取同一原图；
- Agent 重启、离线、恢复和请求取消；
- Range 断点、乱序、背压和 hash 失败；
- 文件被替换但大小和 mtime 不变；
- 缩略图命中不访问 Agent；
- 视频首帧、关键帧和视频原文件分别验证；
- 非法路径、sourceId、过期 session、错误 Token、错误 SPKI；
- 缓存空间不足、`.part` 清理和缓存清空；
- Linux IndexService 完全不存在 Windows 原图路径时仍可通过 Agent 正常读取。

评审结论：架构方向通过；补齐本节约束后进入第一阶段编码。首阶段只实现统一 GUI 模式切换、远程只读索引/搜索和同步协议，不开放远程删除、编辑和 AI 分析。

### 12.9 Remote MainWindow API 映射

| UI 功能 | 本地实现 | 远程首期实现 | 远程 API |
|---|---|---|---|
| 目录树 | SQLite | Gateway | `sources`、`images` |
| 照片网格 | SQLite/ThumbnailService | Gateway + 内容哈希缓存 | `images`、`thumbnails` |
| 搜索 | 本地 FTS | IndexService | `search` |
| 原图 | 本地路径 | AssetSession + MediaAgent | `asset session` |
| 视频首帧/关键帧 | 本地缩略图和关键帧缓存 | IndexService + 内容哈希缓存 | `video thumbnails/keyframes` |
| 视频原文件/播放 | 本地文件 | Range + MediaAgent + 本地缓存 | `video asset session` |
| 视频转写/人物/摘要 | 本地视频索引表 | 远程只读元数据 | `videos/transcripts/faces/summaries` |
| 人物/标签/场景 | 本地 Repository | 只读 Gateway | `people/tags/scenes` |
| 添加目录/扫描 | IndexEngine | 禁用 | 暂不提供 |
| 编辑/分类/删除 | 本地写操作 | 禁用 | 暂不提供 |

远程模式启动 `MainWindow` 时不得构造依赖本地 `PicerDatabase` 的服务；未实现的 RemoteRepository 只能使对应能力禁用，不能导致整个窗口启动失败。

### 12.10 实现一致性约束

- E2E 测试的远程 IP 必须通过测试配置注入，文档中的 `124.156.193.122` 只是部署示例；
- Windows Agent 必须使用 reparse-point 安全的打开方式，在同一只读句柄上完成根目录校验、哈希校验和传输；
- Linux IndexService 永远不依赖 Windows 绝对路径；
- 远程缩略图、原图和视频资源都必须经过 scope、AssetSessionId、Range 和哈希校验。
