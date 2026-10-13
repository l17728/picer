# 本地模型统一平台路由

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
<!-- /historical-status-2026-10-10 -->

Picer 客户端不直接启动 llama.cpp、Whisper、ASR、VL 或 TTS 进程；推理请求经 IndexService/MediaAgent 路由。目标设计由统一模型平台按能力和模型名称调度 GPU/CPU、管理生命周期、并发、日志、版本和健康状态。实现现状与目标差异见下方代码核对补充。

## 代码核对补充（2026-10-04）

- Local GUI 的索引仍由本机 SQLite/`LocalGateway` 提供；AI 请求通过配对的 IndexService endpoint，不存在 GUI 到 `localhost:8000` 的 llama-server 直连回退。
- IndexService 的流式 AI 路由把请求交给在线 MediaAgent；MediaAgent 再向 loopback 模型 endpoint 发 HTTP 请求。当前问答代码只从 MediaAgent 进程环境变量 `PICER_LOCAL_MODEL_ENDPOINT` 读取 endpoint，默认 `http://127.0.0.1:8090/api/vl/chat`，IndexService 广播配置不会覆盖它。独立 llama-server 监听的端口并不自动等于 Picer 已配置的 endpoint；只有 endpoint 确实指向 Console/模型服务时，相关服务才是此链路的依赖。
- 2026-10-08：GUI 人脸训练、校准、历史/制品及重启监控已统一经固定公钥的 IndexService → MediaAgent → 本机 ModelServer；GUI 不再依赖 ModelServer 地址或密钥。详见 [P1 实施与验收](P1_IMPLEMENTATION_2026-10-08.md)。

默认本地网关：`http://127.0.0.1:8090`（生产远程地址必须使用 HTTPS）。客户端只保存服务商、模型和 API 根地址；具体模型文件、端口和运行参数不进入 Picer 代码。现有 8000/8001 独立 llama.cpp 进程属于平台内部实现，不能作为 Picer 配置目标。
