# PLATFORM-HEALTH-001：模型服务健康探针

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

新增 `ModelServiceProbe`，可对训练平台、Qwen3-ASR、Qwen3-VL 和中文 TTS 端点执行轻量 GET 探测，返回连通性、HTTPS 安全状态、延迟和错误类型。远程 HTTP 端点直接判定不安全；本机回环地址允许 HTTP 便于开发。探针只读，不上传照片、音频或向量，可接入诊断中心作为真实服务启动验收前置检查。
