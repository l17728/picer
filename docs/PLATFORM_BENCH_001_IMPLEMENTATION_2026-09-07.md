# PLATFORM-BENCH-001：模型端点基准

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

新增 `EndpointBenchmarkService`，对任意模型端点执行 1–20 次可取消请求并记录成功数、失败类型、P50/P95 延迟。该工具不上传媒体内容，适合在 Qwen3-ASR、Qwen3-VL、中文 TTS 和训练平台实际启动后进行冷/热启动及性能对照；真实 GPU/显存数据仍需平台运行时采集。
