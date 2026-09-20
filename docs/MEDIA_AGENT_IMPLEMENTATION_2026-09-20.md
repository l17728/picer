# 常驻原图/原视频代理实现记录（2026-09-20）

## 已完成

- 新增 `Picer.MediaAgent` Windows systray 程序，使用与 Picer 相同的 HTTPS 端点建立 WSS 连接。
- 代理发送 `agent.register`、周期 `agent.heartbeat`，服务端维护在线代理注册表。
- 服务端新增 `GET /api/gateway/v1/assets/images/{id}?agentId=...` 和视频同类端点；索引服务只读取索引记录中的路径，不直接访问客户端磁盘。
- 代理端对路径执行根目录白名单校验，限制单文件 64 MiB，返回 MIME、请求 ID 和 Base64 内容；WebSocket 支持分片文本消息。
- `HttpIndexGateway`/`LocalGateway` 提供原图和原视频打开能力。远程浏览窗口双击缩略图时通过代理取原图，并按 SHA-256 使用内容寻址缓存；无哈希记录时使用临时文件。

## 配置

- `PICER_INDEX_SERVICE_IP`：索引服务 IP（不包含协议和端口）。
- `PICER_INDEX_SERVICE_TOKEN`：与 IndexService 相同的 Bearer token。
- `PICER_MEDIA_AGENT_ROOTS`：本机允许代理读取的根目录，多个目录用 `;` 分隔。未设置时回退到 `PICER_DATA_ROOT`。

## 安全和限制

- 服务端仍由 `/api` Bearer 鉴权保护；代理只接受服务端通过已认证 WebSocket 下发的请求。
- 代理不会暴露任意路径；必须位于白名单根目录下，且文件大小不超过 64 MiB。
- 当前版本是单租户、单请求响应通道；大视频的完整播放/HTTP Range 仍需后续改为二进制分块协议，避免 Base64 内存峰值。
- 代理尚未由 GUI 安装器自动注册为开机启动；可由 GUI 后续设置项启动/停止 systray 进程。

## 验证

- `dotnet build picer.sln -c Debug --no-restore --warnaserror` 已通过。
- 已有远程网关和 `RemoteAssetCache` 回归测试继续保留；需要在真实两台机器上补做：同步索引 → 启动代理 → 远程双击图片/视频 → 再次打开命中缓存。
