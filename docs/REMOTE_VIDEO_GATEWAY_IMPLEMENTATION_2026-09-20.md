# 远程视频 Gateway 元数据实现

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

日期：2026-09-20

本阶段将视频记录纳入 `IIndexGateway`，与图片 Gateway 使用相同的分页和 source 过滤语义：

- `ListVideosAsync`：本地 `VideoRepository` 和 HTTPS IndexService 均支持分页；
- `GetVideoAsync`：本地/远程按视频 ID读取元数据；
- Named Pipe Gateway 同步支持上述操作；
- IndexService 不接受或使用客户端提交的绝对路径。

本阶段只传输视频元数据，视频首帧/关键帧和原文件仍由后续 MediaAgent 资源协议实现。整套解决方案已通过 `--warnaserror` 构建；Gateway 配置测试 8 项和 ServiceBrowse E2E 1 项通过。
