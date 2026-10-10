# 待处理人脸摘要即时同步（2026-09-23）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

## 问题

待分类、待确认或待命名人脸完成操作后，右侧列表会先移除，但左侧“待处理人脸”按钮仍显示旧计数，容易让用户误以为操作没有生效。

## 实现

- 操作开始时立即将摘要切换为“正在同步…”，让用户看到操作已被接受。
- 在后台按当前模型空间重新读取 `GetFaceWorkQueueCounts`，更新总数、待确认、待命名和待分类计数。
- 查询延迟 150ms，避开正在提交的事务；后续权威查询不会阻塞 UI。
- 模型空间切换期间丢弃旧查询结果，避免 SFace/InsightFace 的计数串写。
- 记录摘要刷新成功或失败日志，便于定位状态不同步问题。

## 验证

- `dotnet build picer.sln -c Debug --no-restore --warnaserror` 通过（0 警告、0 错误）。
- 已提交并推送：`73b82a7 fix: refresh pending face summary after operations`。
