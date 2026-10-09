# 远程原图/视频打开路径修复（2026-09-30）

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

## 根因

- 主图库图片双击命令在 `ServiceMode` 下直接提示“远程模式不能在本机打开源文件”，提前 return；没有调用 `IMediaAssetGateway`，因此 IndexService 与源机器 MediaAgent 的原图代理链路根本未被触发。
- 视频页已有 IndexService `assets/videos/{id}` 代理，但只识别 `HttpIndexGateway`，并且缓存键只有视频 ID；网关类型不匹配时会误报代理未连接，源视频变化后也可能复用旧缓存。
- IndexService 的图片与视频原图接口都通过在线 MediaAgent 取源文件；远程 GUI 只能将响应落入本机缓存后再交给系统播放器/图片查看器，不应尝试用索引中的源路径本机打开。

## 修复

- 主图库远程双击改为调用 `IMediaAssetGateway.OpenOriginalImageAsync` 或 `OpenOriginalVideoAsync`，缓存到 `remote-assets/images` / `remote-assets/videos`，下载完成后再打开缓存文件。
- 视频页改用统一媒体网关接口；图片与视频缓存键加入文件大小、源修改时间，避免索引媒体更新后继续打开旧缓存。
- 两处均采用临时文件写入后原子替换，校验下载长度，并记录 operation id、媒体类型、索引 ID、缓存命中及失败阶段；不记录原始路径。

## 回归

- `GatewayContractTests.RemoteAgent_ProxiesOriginalImageThroughIndexService` 验证图片经在线 MediaAgent 中转，并覆盖单代理自动选择。
- `GatewayContractTests.RemoteAgent_ProxiesOriginalVideoWithoutClientAgentId` 验证远程 GUI 不提供 agent ID 时，视频仍经 IndexService 路由到 MediaAgent 并返回原始字节。
- WPF `--warnaserror` 构建通过；远程原图/视频代理契约用例通过。
