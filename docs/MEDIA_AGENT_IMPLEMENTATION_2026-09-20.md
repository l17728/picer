# 常驻原图/原视频代理实现记录（2026-09-20）

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

## 已完成

- 新增 `Picer.MediaAgent` Windows systray 程序，使用与 Picer 相同的 HTTPS 端点建立 WSS 连接。
- 代理发送 `agent.register`、周期 `agent.heartbeat`，服务端维护在线代理注册表。
- 服务端新增 `GET /api/gateway/v1/assets/images/{id}?agentId=...` 和视频同类端点；索引服务只读取索引记录中的路径，不直接访问客户端磁盘。
- 代理端对路径执行根目录白名单校验，限制单文件 64 MiB，返回 MIME、请求 ID 和 Base64 内容；WebSocket 支持分片文本消息。
- `HttpIndexGateway`/`LocalGateway` 提供原图和原视频打开能力。远程浏览窗口双击缩略图时通过代理取原图，并按 SHA-256 使用内容寻址缓存；无哈希记录时使用临时文件。
- 索引服务窗口新增本机代理配置：填写 IndexService IP 后可启动/重启托盘代理，读取本地已登记来源目录作为白名单；配置保存到 `media-agent.json`。发布脚本会把代理放入 GUI 发布目录的 `media-agent/` 子目录。

## 配置

- `PICER_INDEX_SERVICE_IP`：索引服务 IP（不包含协议和端口）。
- `PICER_INDEX_SERVICE_TOKEN`：与 IndexService 相同的 Bearer token。
- `PICER_MEDIA_AGENT_ROOTS`：本机允许代理读取的根目录，多个目录用 `;` 分隔。未设置时回退到 `PICER_DATA_ROOT`。

## 安全和限制

- 服务端仍由 `/api` Bearer 鉴权保护；代理只接受服务端通过已认证 WebSocket 下发的请求。
- 代理不会暴露任意路径；必须位于白名单根目录下，且文件大小不超过 64 MiB。
- 当前版本是单租户、单请求响应通道；大视频的完整播放/HTTP Range 仍需后续改为二进制分块协议，避免 Base64 内存峰值。
- 当前由 GUI 窗口启动/重启代理，不注册 Windows 开机启动；勾选“索引同步后保持代理运行”只保存用户意图，后续同步流程可复用该配置。

## 验证

- `dotnet build picer.sln -c Debug --no-restore --warnaserror` 已通过。
- 已有远程网关和 `RemoteAssetCache` 回归测试继续保留；需要在真实两台机器上补做：同步索引 → 启动代理 → 远程双击图片/视频 → 再次打开命中缓存。
