# 操作日志规范（2026-09-15）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

Picer 保留原有文本日志，并新增按天滚动的结构化操作日志：

- 文本日志：`<数据根目录>/logs/picer-YYYY-MM-DD.log`
- 操作日志：`<数据根目录>/logs/picer-operations-YYYY-MM-DD.jsonl`
- 崩溃日志：`<数据根目录>/logs/crash.log`

关键后台操作（索引、AI 问答、人脸分析/归类）会记录 operationId、开始/结束
状态、耗时、进程/线程、有限的计数和异常类型。相同 operationId 可把 UI 状态、
后台任务和错误关联起来。

日志不得写入人物姓名、AI 原始问题、凭据、访问令牌或人脸向量；字段值统一
截断并脱敏。诊断包应优先选择脱敏 JSON/CSV 导出，不直接复制数据库或原始照片。

