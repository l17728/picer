# 本机索引 Relay 重构启动记录（2026-11-19）

本文档使用 UTF-8 编码。专项设计以 `LOCAL_INDEX_RELAY_SDD_2026-11-19.md` 为准，验收以 `LOCAL_INDEX_RELAY_E2E_2026-11-19.md` 的 RELAY-01 至 RELAY-40 为准；全局数据安全和数据库约束继续遵守 `docs/SDD.md`。

## 当前审计结论

IndexService 已存在以 `space_id` 为核心的多租户授权和运行时隔离基础，包括设备授权、空间运行时池、来源路由、同步接收器和空间级清理。Relay 重构必须复用该边界，不新增平行租户标识。

## 开发顺序

1. 定义索引模式状态机和持久化配置；
2. 抽象 Relay Connector 长连接和请求幂等层；
3. 将 GUI 查询路由接入本机/服务索引开关；
4. 实现切换同步、一致性检测、只读探针和远程副本清理；
5. 接入媒体文件自动加密与本机解密流；
6. 增加多租户 Relay 路由校验；
7. 执行 RELAY-01 至 RELAY-40；
8. Release 构建前提交、推送和验收。

## 不变约束

- SQLite、FTS5、WAL/SHM 仍在本地磁盘；
- 原图和缩略图不进入 Relay 持久化；
- 数据库不加密，媒体文件内容自动选择高性能加密算法；
- 模式切换失败不得删除远程索引；
- 每阶段完成后必须独立提交并推送。

## 2026-11-19 实施进度：模式切换一致性闸门

- 本地模式 `SyncPolicy=switch-only`，不启动持续索引同步、通知监听或后台发布循环。
- 服务模式保留持续同步；切换模式前执行一次完整同步，取消、超时、认证失败或一致性不匹配均保持原模式，不执行远端删除。
- 新增 `IndexModeSwitchTransaction`，强制执行 Begin → Syncing → 一致性通过 → Switched → Cleanup 的顺序。
- 新增 `IndexConsistencySnapshotBuilder`：采集 epoch、媒体/元数据 revision、媒体数量、媒体摘要、元数据摘要和待写队列，并在切换前比较本地与远端证据。
- `BackendSettingsWindow` 只有在切换闸门完成后才写入新配置并重启；配置失败不会覆盖当前配置。
- 验证：`IndexConsistencySnapshotTests`、`IndexModeSwitchTransactionTests`、`IndexBackendConfigTests` 及相关切换配置回归共 13 项通过；Release GUI 构建路径为 `src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe`，构建日志为 `artifacts/build-relay-switch-final-20261119.log`。

### 后续必须保持的验收边界

服务索引被取消后不做持续同步；再次切回服务模式时仍必须执行且仅执行本次切换所需的一次完整同步，并通过一致性闸门后才允许切换。只有实际发生本地/服务模式变化才进入切换事务；同一模式重复保存不会伪造切换，也不会额外同步。

- 目标服务地址现在在切换同步前先通过已验证连接确定；不会把一次性同步发送到旧的配对端点。
- 一致性通过后增加只读探针：目录、标签、相册、空搜索和首张缩略图读取；探针失败会阻止切换。
- 一次性同步调用链已去除重复的元数据/人物审核写入：增量发布器负责各写入队列一次，切换编排器不再重复提交。
- 本地 IndexService 的 named-pipe 网关补齐目录查询操作，使切换闸门的目录探针在 HTTPS 与 pipe 两种服务入口行为一致。
- 账号设备在设置窗口刚完成配对时，切换编排器会先重新加载本地发布器配置，再执行一次同步，避免必须重启 GUI 才能发现新凭据。
- 首次从本地模式切换到新服务时，若本机没有旧配对端点，编排器直接使用当前已验证的目标地址完成一次同步，不再错误地要求先存在旧同步配置。
- 服务模式切换到本地模式时，成功同步、一致性校验和配置写入后登记重启后清理；下一次本地启动删除 `remote-mirror.db` 及远程缓存，失败时保留标记等待重试，且不会删除本地 `picer.db`。
- `RemoteMirrorCleanupTests` 验证清理会删除远程镜像文件和缓存，但保留本地 `picer.db`；清理标记解析失败或删除失败时保留标记，下一次启动重试。
- 清理路径安全测试同时覆盖正常清理和越界路径拒绝（`RemoteMirrorCleanupTests` 2/2）。
- 远程 GUI 主窗口标题现在显示 `远程：服务地址`，用于确认当前连接的服务器，不再只显示“远程”。

## 最终构建验收记录

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：0 警告、0 错误。
- 切换/配置/一致性/pipe 探针测试：13/13 通过。
- 远程镜像清理 E2E：2/2 通过。
- 正式 GUI：`src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe`，Release 构建日志：`artifacts/build-relay-switch-final-20261119.log`。
- 媒体通道修复后的最新 Release 构建同样通过 0 警告、0 错误；正式 GUI 路径保持不变，构建日志为 `artifacts/build-media-close-release-20261119.log`。构建后已重新启动该 GUI 和 `src/Picer.MediaAgent/bin/Release/net8.0-windows/Picer.MediaAgent.exe`。

### 当前测试边界

切换相关定向测试、`SpaceSyncReceiverTests` 全类和配对/镜像清理 E2E 均可重复通过；`SpacePublisherIntegrationTests.AccountSourcePurgeUsesStableIdentityAndRecoversWithoutTouchingAnotherSourceOrOriginal` 的 4 个参数场景全部通过，总耗时约 9 分钟。早先使用 90 秒无活动 blame 阈值会误中止仍在运行的长集成测试；整套 `Picer.Tests` 的长运行未取得最终汇总文件，因此不将其记为全套通过，也不把该现象误判为产品故障。

## 2026-11-25 运行架构审计与缺项

- 远程浏览的权威数据源是服务器 IndexService。主画廊、目录照片及分类树数据、视频列表/详情、标签、相机筛选、人物选项和人脸模型版本/覆盖数已改为直接读取 IndexService；实际 GUI 验收确认远程图库 8,603 张、视频 529 个、人物页模型/历史选项及 28,925 张人脸统计可见。`remote-mirror.db` 仍承载部分编辑/兼容数据，人物脸列表与训练历史等路径仍有镜像依赖，故不能将所有人物页功能记为直读完成。
- 当前 `SpaceWebSocketRelay` 是账号服务与 MediaAgent 原图/来源通道的按请求双向代理；尚未发现本机 IndexService 主动建立的持久 Relay Connector、云 Relay 设备注册/配对/多路复用协议，或 GUI 经 Relay 直达来源 IndexService 的传输。
- 当前可见的模式切换同步闸门、一致性快照、只读探针及镜像清理不等于完整 RELAY-01 至 RELAY-40 验收。
- `SpaceRuntimePool` 和账号授权提供空间级运行时/设备授权隔离基础；尚未证明 Relay 请求的 tenant/device/session 全绑定、缓存/媒体隔离、租户级任务队列、撤销后既有流即时终止等全部验收。
- 本次检查未发现针对所有服务模式媒体缓存文件的内容加密实现，也未发现满足 SDD 删除确认令牌、审计、Relay 开关、迁移/恢复任务要求的完整索引管理控制台。
- 40 项 E2E 表中只有切换/同步相关用例具有当前自动化覆盖证据；Relay 长连接、密文透传、零落盘、重放、背压、加密缓存和 Web 管理仍未逐项验收。

### 2026-11-26 Relay 连接身份与控制通道首片

- 新增 `index-service` 设备类型，必须由空间所有者批准，只签发 `relay.connect`，批准表单明确它不能读取/修改索引或访问驻留程序原图接口。其 access/refresh 生命周期按长期服务设备设置；撤销仍沿用现有授权 epoch。
- `DeviceAccountClient` 只允许该设备类型连接 `/api/index/v1/connect`；驻留 `agent` 的 `/api/agent/v1/connect` 与媒体 socket 权限保持独立。
- 账号服务新增空间隔离的连接注册表，在线 socket 不申请云端 `SpaceRuntimePool`，每设备最多一条，独立限流。协议第一版只接受有界心跳控制帧，记录连接时间/最近心跳；设备撤销由现有后台授权观察器关闭连接。账号门户设备页显示该服务设备的在线/离线状态。
- `AccountAuthorizationTests` 验证即使批准表勾选编辑，也只发 `relay.connect`；`AccountMediaRoutingTests.IndexServiceConnectorUsesDedicatedRelayGrantAndHeartbeatSocket` 验证配对、心跳应答、云索引请求拒绝、MediaAgent socket 拒绝、撤销断流和注册状态清理。
- 本切片是身份与控制通道，不是完整 Relay：本机 IndexService 出站 worker/凭据存储、请求/响应多路复用、端到端密文 payload、GUI 路由、恢复幂等和真正远程查询尚未接入。不能标记 RELAY-02、RELAY-03 或 RELAY-07 已完成；实现报告见[Relay 连接身份首片](LOCAL_INDEX_RELAY_CONNECTOR_IDENTITY_2026-11-26.md)。

### 2026-11-26 本机 Connector 凭据与常驻心跳

- 本机 IndexService 已新增 Linux 可用的 `IndexRelayDeviceCredentialStore`，可从可信 `ServiceDiscovery` 创建 `index-service` 设备身份、发起配对并导入批准文件；私钥与设备公钥绑定校验，Unix 文件权限固定为目录 0700、凭据 0600，拒绝宽权限凭据。
- 独立 IndexService 在数据根中发现 `relay-connector.json` 后启动单一后台连接器，可由 `PICER_INDEX_RELAY_CREDENTIAL_PATH` 覆盖路径。连接器使用证书 SPKI pin、DPoP 和现有 token refresh，常驻 WebSocket 每 15 秒心跳并以指数退避重连；隔离租户运行时不启动此 worker。状态通过本地 bearer token 保护的 `/api/index-relay/v1/status` 查询。
- 配对/凭据/心跳/关停定向测试 4/4；Debug 整解方案严格构建 0 警告/错误；IndexService Release 0 警告/错误，DLL SHA-256 `F4F73443837A1F33AA223D02CBE76662A56574E7BBF3C731A4F4A7B08E2150DD`。详细边界见[本机 Connector 运行时报告](LOCAL_INDEX_RELAY_CONNECTOR_RUNTIME_2026-11-26.md)。
- 仍未实现请求/响应多路复用、GUI 查询路由、端到端密文 payload、数据请求幂等和恢复、生产配对入口及完整 tenant/device/session 验收。由于 socket 目前仅传心跳，本阶段没有部署到生产服务；不得将完整 Relay 或 RELAY-01 至 RELAY-40 标为完成。

### 2026-11-26 端到端加密会话与密文帧原语

- 新增签名 P-256 临时 ECDH 交换，绑定 space、session、双方设备 ID 和有效期；根据双方已认证设备签名公钥验证交换，HKDF-SHA256 派生方向分离的 AES-256-GCM 密钥。
- 密文帧 AAD 绑定租户、双方设备、session、单调序列号、request ID、幂等键、帧类型与过期时间；请求正文/路径可放在加密 payload。限制明文帧 2 MiB，接收端具备 4096 序号滑动重放窗，允许并发帧乱序。
- `RelaySessionCryptoTests` 3/3；连接器/凭据和协议合并测试 7/7；整解方案严格构建 0 警告/错误。Release IndexService DLL SHA-256 `9B6F6495740D546196615A30619C0644302B0A9A0D15DD8FCBD63E740EA0FFE2`。
- 该切片尚未接入 Relay WebSocket；当前交换仅是可调用的 Core 协议原语，可信 peer key 的账号目录分发、云端路由、多路复用、写事务持久幂等账本、GUI/IndexService 接入及压力/撤销验收仍未完成。生产环境没有部署或启用数据帧；见[协议原语说明](LOCAL_INDEX_RELAY_PROTOCOL_V1_2026-11-26.md)。

### 2026-11-25 远程 GUI 直读服务端索引的增量

- 主画廊搜索/翻页、目录树的来源/目录、目录照片/视频和视频列表/详情已增加 HTTP IndexService 直读路径；这些浏览操作不再等待本地镜像同步。
- 相册和地理分类树改为读取新的轻量 `browser-metadata` 服务端快照（相册、相册成员 ID、位置层级、相机值），分类照片通过 `ImageIds` 搜索过滤在服务端分页读取，避免从镜像库取内容或对每张照片发起单独请求。
- 远程标签和人物查询选项改为直接读取 IndexService；远程人物名字的主搜索过滤使用服务器返回的人物 ID，避免从镜像库解析人物后把过期 ID 发给服务器。
- 远程人脸模型版本/覆盖数量从轻量目录接口读取；人物页的模型版本下拉不再通过镜像库冷读。
- 远程视频详情中的人物裁剪图现在复用该视频已从服务端加载并进入共享缓存的关键帧，不再为每个人脸回查本地镜像的帧引用。
- 新读端点已加 HTTP/pipe gateway 路径及账号服务 `index.read` 权限/快照支持；定向 Gateway HTTP、pipe、本地契约测试和 `ImageIds` 分页过滤测试通过。已部署到 `47.103.99.229`，第二次 deployment ID `20261125T114630214Z`，Linux x64 IndexService SHA-256 `50f5e0f1f2c88c6d409033e87ba95b8032d929d5d484ff6fa69497cfb38a1c70`。GUI Release 路径 `src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe`，当前 SHA-256 `DE9452AD200D2136AF05A11E8BE8C63DBC39D4C099C1936C55E5CC14E5A0683A`；GUI 和 MediaAgent 均已从正式 Release 路径启动。
- 远程人物页脸列表、分区统计、训练记录和模型切换覆盖率仍有镜像依赖；远程搜索别名解析也尚未纳入轻量人物目录协议。人物页控件挤压已由分行布局修复；UI 中已看到模型/训练/人物下拉值及 5,000 张人脸缩略图，但 18,053 分区总数与 7,551 待处理人脸之间有 13 项统计差异。后续需将人物浏览改为服务器分页查询并从同一服务端快照计算分区，解决差异；不得将所有远程 UI 记为直读完成。

本轮提交 `752aee2`、`f276311`、`7e71d9b`、`54cd4fb` 均已推送到 `origin/main`；此前直连与备用路由的暂时性 443 超时后，直连重试成功。工作区与远端已同步。

以上缺项保持为当前工作，不将旧的阶段构建记录推断成完整 Relay 交付。详细症状与本轮启动修复见[远程镜像首屏加载报告](REMOTE_MIRROR_STARTUP_LOADING_FIX_2026-11-25.md)。

### 2026-11-25 远程人物浏览直读补充

本节覆盖本文件此前关于人物列表仍读 `remote-mirror.db` 的状态描述。远程人物浏览快照现在由 IndexService 的权威索引生成，覆盖人物目录、人脸列表视图、分区/摘要及训练历史。GUI 将其用于远程浏览；远程浏览不需要等待或依赖完整本地镜像同步。镜像保留给本地模式、离线兼容和需要本地计算/提交的操作，不作为远程浏览的权威数据副本。

- 变更提交：`f562dfa fix: read remote people browsing from index service`，已推送 `origin/main`。
- 验证：HTTP、named-pipe、本地 gateway 契约测试 78/78；Debug 整解方案、GUI Release、IndexService Release 均无警告/错误。
- 部署：`20261125T122655387Z`，Linux x64 IndexService SHA-256 `92d8713823f67301190c9b899cf90e8bc2429266c59d0e0c478d3195c5409623`。GUI Release SHA-256 `0855D9EAF7D0484F8CA2540DA96823BE7643570E2DBECC056DD0B2EC7036A4DD`，已重启并连接新服务。
- UI 实测：人物模型、训练历史、命名人物选择器、服务端人脸统计及分页列表可见；照片人脸缩略图通过共享远程缓存异步显示。
- 缺项：远程命名人物详情尚未接入服务端视频人脸时间线；人物操作中的本地训练/补算/写入仍是本地工作流。Relay E2E-01 至 E2E-40 尚未整体通过，不能据此声明 Relay SDD 全部完成。

### 2026-11-26 已命名人物视频人脸

远程命名人物详情现从服务端人员快照获取已确认视频人脸记录及视频/关键帧引用，GUI 通过服务端关键帧媒体路由和统一远程缓存取图，不再向镜像数据库查找视频关联。Gateway HTTP、pipe 和 local 三种契约测试均覆盖这一查询，3/3 通过。

- 提交 `364163c` 已推送 `origin/main`。
- Debug 严格构建、GUI Release、IndexService Release 均为 0 警告/0 错误。
- 新服务器部署 ID `20261125T125625883Z`，IndexService Linux x64 SHA-256 `8622ef86b95b6dfe537a342eaf211cffa92c4afe38b5f21b4285713870f6801b`，部署脚本 `Upgrade verified`。正式 GUI 路径 `src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe`，SHA-256 `4A8CE90D23CA4BAC7F2667ED7A0C4C9561DC1B8372B6D6D2E8F0163B9D6C1624`。
- 保留边界：集成测试证明查询数据契约，不证明当前服务器上任一人物恰有视频脸，也不代表整个 Relay SDD 的持久连接、隔离、加密和管理控制台已完成。

### 2026-11-26 一致性快照竞态保护

- `IndexConsistencySnapshotBuilder.CaptureRemoteAsync` 原先先读取服务端 revision，再分别分页读取来源/媒体并读取元数据；这期间若服务端发生写入，方法仍会返回不同时间点拼接的快照，模式切换的一致性闸门可能基于非原子证据放行。
- 现在快照采集结束后再次读取 epoch、媒体 revision 和 metadata revision；任一项变化即抛出 `index_consistency_snapshot_changed`，切换事务失败关闭，原有模式不变。用户可在数据静止后重试。
- `IndexConsistencySnapshotTests` 覆盖稳定性比较及真实 `CaptureRemoteAsync` 在分页期间 revision 变化时拒绝返回快照，3/3 通过。
- 本次只改一致性证据验证，不改变持续同步策略或服务端数据；无需部署 IndexService。

### 2026-11-26 账号发布器切换完整性门禁

账号发布器路径此前绕过本地/远端快照门禁。本阶段要求完整来源范围、媒体及元数据 revision 检查点、待处理队列与冲突清空、服务端 publisher 检查点相同，并逐项验证目标服务端来源路径和既有只读探针。详细实现及测试证据见[账号发布器切换验证报告](INDEX_MODE_SWITCH_ACCOUNT_VERIFICATION_2026-11-26.md)。切换相关状态机/配置/快照/清理测试 15/15、账号来源清理的四个长参数场景 4/4 通过。

此实现仅闭合现有服务索引切换的一处门禁缺口；持久 Relay Connector、端到端密文通道、媒体缓存内容加密、Relay 零落盘和 Web 管理控制台等仍未开发/验收，不能据此标记 RELAY-01 至 RELAY-40 完成。

### 2026-11-26 IndexService 缩略图缓存静态加密

IndexService 缩略图缓存已加入每数据根的稳定密钥、AES-256-GCM/XChaCha20-Poly1305 算法回退、按缓存文件名绑定的认证数据、HTTP 读取解密、旧明文缓存惰性迁移和启动后台迁移。已部署 ID `20261126T044027575Z`，Linux x64 IndexService SHA-256 `1f888396e0ab37bd90a1a1e010bbcda058227014276db0624c5885afa3efbab0`。完整范围、测试边界与尚未加密的媒体/客户端缓存见[缩略图缓存加密报告](MEDIA_CACHE_ENCRYPTION_2026-11-26.md)。这只完成服务器缩略图磁盘缓存一项，不能表述为 Relay 媒体加密已闭环。

### 2026-11-26 GUI 远程派生媒体缓存静态加密

此条更新前述“客户端缓存仍未加密”的状态：GUI ServiceMode 的共享远程缩略图、人脸裁剪、视频关键帧和视频摘要拼图现使用同一 DPAPI CurrentUser 数据密钥，磁盘缓存采用 PICERENC2 认证加密；旧缩略图和拼图会后台或按需迁移。视频摘要为兼容当前模型文件路径接口，会短暂解密到系统临时目录，任务完成/异常/取消时删除；强杀或系统崩溃时可能残留。具体证据见[GUI 媒体缓存加密报告](CLIENT_MEDIA_CACHE_ENCRYPTION_2026-11-26.md)。这闭合当前发现的 GUI 派生媒体缓存静态加密缺项，不等于端到端 Relay 加密、零落盘或 RELAY-01 至 RELAY-40 全部完成。
