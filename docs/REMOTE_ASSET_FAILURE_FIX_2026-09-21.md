# 远程原图打开失败修复（2026-09-21）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

## 根因

远程浏览窗口把当前 GUI 的 `Environment.MachineName` 当作图库主机的
MediaAgent ID。两台机器或代理重连后，服务端找不到该 ID，原图请求会等待
代理会话超时；旧服务端还会在未传 agentId 时直接返回 `agentId_required`。

## 修复

- 远程 GUI 不再伪造或强制传入本机机器名，交由单租户 IndexService 选择在线代理。
- IndexService 在没有指定 ID、或指定 ID 已断线时，仅当在线代理唯一时安全回退到该代理；
  多个代理时不猜测，返回 `media_agent_offline`。
- 图片和视频资源路由允许省略 `agentId`，离线时返回可诊断的 503 JSON，而不是模糊的空响应。
- 内容哈希缓存逻辑不变，成功后第二次打开继续命中本地缓存。

## 验证

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：通过。
- `GatewayContractTests`：47/47 通过。
- 已用真实 IndexService + MediaAgent 回源读取同步索引中的 GIF 原图，HTTP 200，
  Content-Length 与原文件一致。
