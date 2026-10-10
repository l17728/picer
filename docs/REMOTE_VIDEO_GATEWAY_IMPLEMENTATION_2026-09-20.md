# 远程视频 Gateway 元数据实现

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

日期：2026-09-20

本阶段将视频记录纳入 `IIndexGateway`，与图片 Gateway 使用相同的分页和 source 过滤语义：

- `ListVideosAsync`：本地 `VideoRepository` 和 HTTPS IndexService 均支持分页；
- `GetVideoAsync`：本地/远程按视频 ID读取元数据；
- Named Pipe Gateway 同步支持上述操作；
- IndexService 不接受或使用客户端提交的绝对路径。

本阶段只传输视频元数据，视频首帧/关键帧和原文件仍由后续 MediaAgent 资源协议实现。整套解决方案已通过 `--warnaserror` 构建；Gateway 配置测试 8 项和 ServiceBrowse E2E 1 项通过。
