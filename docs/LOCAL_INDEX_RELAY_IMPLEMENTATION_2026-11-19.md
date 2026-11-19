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
- 验证：`IndexConsistencySnapshotTests`、`IndexModeSwitchTransactionTests`、`IndexBackendConfigTests` 共 12 项通过；Release GUI 构建路径为 `src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe`，构建日志为 `artifacts/build-index-consistency-gate-20261119.log`。

### 后续必须保持的验收边界

服务索引被取消后不做持续同步；再次切回服务模式时仍必须执行且仅执行本次切换所需的一次完整同步，并通过一致性闸门后才允许切换。

- 目标服务地址现在在切换同步前先通过已验证连接确定；不会把一次性同步发送到旧的配对端点。
- 一致性通过后增加只读探针：目录、标签、相册、空搜索和首张缩略图读取；探针失败会阻止切换。
- 一次性同步调用链已去除重复的元数据/人物审核写入：增量发布器负责各写入队列一次，切换编排器不再重复提交。
- 本地 IndexService 的 named-pipe 网关补齐目录查询操作，使切换闸门的目录探针在 HTTPS 与 pipe 两种服务入口行为一致。
- 账号设备在设置窗口刚完成配对时，切换编排器会先重新加载本地发布器配置，再执行一次同步，避免必须重启 GUI 才能发现新凭据。
- 首次从本地模式切换到新服务时，若本机没有旧配对端点，编排器直接使用当前已验证的目标地址完成一次同步，不再错误地要求先存在旧同步配置。
