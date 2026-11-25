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

### 2026-11-25 远程 GUI 直读服务端索引的增量

- 主画廊搜索/翻页、目录树的来源/目录、目录照片/视频和视频列表/详情已增加 HTTP IndexService 直读路径；这些浏览操作不再等待本地镜像同步。
- 相册和地理分类树改为读取新的轻量 `browser-metadata` 服务端快照（相册、相册成员 ID、位置层级、相机值），分类照片通过 `ImageIds` 搜索过滤在服务端分页读取，避免从镜像库取内容或对每张照片发起单独请求。
- 远程标签和人物查询选项改为直接读取 IndexService；远程人物名字的主搜索过滤使用服务器返回的人物 ID，避免从镜像库解析人物后把过期 ID 发给服务器。
- 远程人脸模型版本/覆盖数量从轻量目录接口读取；人物页的模型版本下拉不再通过镜像库冷读。
- 远程视频详情中的人物裁剪图现在复用该视频已从服务端加载并进入共享缓存的关键帧，不再为每个人脸回查本地镜像的帧引用。
- 新读端点已加 HTTP/pipe gateway 路径及账号服务 `index.read` 权限/快照支持；定向 Gateway HTTP、pipe、本地契约测试和 `ImageIds` 分页过滤测试通过。已部署到 `47.103.99.229`，第二次 deployment ID `20261125T114630214Z`，Linux x64 IndexService SHA-256 `50f5e0f1f2c88c6d409033e87ba95b8032d929d5d484ff6fa69497cfb38a1c70`。GUI Release 路径 `src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe`，SHA-256 `2A0E3563F2F229BFFB8B61BEC9E3D7AD7B29DB31B2617CC315F5DA90BFCB0388`；GUI 和 MediaAgent 均已从正式 Release 路径启动。
- 远程人物页脸列表、训练记录和模型切换覆盖率仍有镜像依赖；远程搜索别名解析也尚未纳入轻量人物目录协议。实际 GUI 检查时人物统计已出现，但分类列表区域仍在加载且版面挤压，需单独定位。不得将“所有远程 UI 已完全直读”记为完成。

本轮提交 `752aee2`、`f276311` 已在本地；向 `origin/main` 推送遇 GitHub 443 连接超时/重置，仓库暂时领先远端两个提交。按 `docs/ENGINEERING_MEMORY.md` 的备用路由继续重试，不更改 origin、hosts 或证书检查。

以上缺项保持为当前工作，不将旧的阶段构建记录推断成完整 Relay 交付。详细症状与本轮启动修复见[远程镜像首屏加载报告](REMOTE_MIRROR_STARTUP_LOADING_FIX_2026-11-25.md)。
