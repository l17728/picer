# 媒体预览长连接复用实现报告（2026-11-18）

## 目标

缩略图和预览请求复用 MediaAgent 已建立的常驻控制 WebSocket，避免每次请求重新建立临时媒体连接和重复握手。

## 实现

- MediaAgent 增加 `media.preview` 请求处理，在现有驻留连接上并发处理预览任务。
- IndexService 为每个请求分配 requestId，通过同一 AgentSession 的发送锁复用控制通道，并按 requestId 完成响应。
- 缩略图与 1600px 预览优先走长连接；旧版驻留程序或长连接失败时保留原有媒体流/asset fallback。
- 原图大文件继续使用范围读取数据流，避免把大文件编码进控制通道。
- 预览失败和回退路径记录类型化日志，便于比较握手、读取、解码和传输耗时。

## 验证

- `dotnet build src/Picer.MediaAgent/Picer.MediaAgent.csproj -c Release --no-restore --warnaserror`：通过。
- `dotnet build server/Picer.IndexService/Picer.IndexService.csproj -c Release --no-restore --warnaserror`：通过。
- 运行时需验证同一驻留连接连续处理多个预览请求，日志不应为每张缩略图新增 `agent_socket_connected`。
