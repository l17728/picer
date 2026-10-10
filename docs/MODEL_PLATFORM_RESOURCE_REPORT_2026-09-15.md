# 模型平台资源与运行报告（2026-09-15）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

## 当前实机状态

| 项目 | 结果 |
|---|---|
| GPU | NVIDIA GeForce RTX 4060 Ti 16GB |
| 显存总量 | 16,380 MiB |
| 当前显存占用 | 11,456 MiB |
| GPU 利用率 | 9%（采样时） |
| GPU 温度 | 50°C（采样时） |
| 系统内存 | 16 GiB，总占用约 8.5 GiB（采样时） |
| 磁盘 | 总 500 GB，可用约 401 GB |

## 服务

- Qwen3-VL 8B：`ready`，GPU 常驻，估算显存 7,800 MiB。
- Qwen3-ASR 1.7B：`ready`，GPU 常驻，估算显存 3,900 MiB。
- 中文 TTS：`ready`，CPU 常驻。
- Picer ModelServer：`ready`，CPU Worker，平台后端端口 8091，自动重启开启。

以上数据来自统一模型平台 `/api/status` 的实机采样，不代表长时间平均值。
正式发布前仍需用真实图片、视频和语音样本记录延迟、峰值显存、吞吐和并发
限制，并将结果纳入目标机器的发布门禁。

