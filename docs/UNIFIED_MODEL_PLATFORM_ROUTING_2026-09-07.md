# 本地模型统一平台路由

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

Picer 客户端不直接启动 llama.cpp、Whisper、ASR、VL 或 TTS 进程；推理请求经 IndexService/MediaAgent 路由。目标设计由统一模型平台按能力和模型名称调度 GPU/CPU、管理生命周期、并发、日志、版本和健康状态。实现现状与目标差异见下方代码核对补充。

## 代码核对补充（2026-10-04）

- Local GUI 的索引仍由本机 SQLite/`LocalGateway` 提供；AI 请求通过配对的 IndexService endpoint，不存在 GUI 到 `localhost:8000` 的 llama-server 直连回退。
- IndexService 的流式 AI 路由把请求交给在线 MediaAgent；MediaAgent 再向 loopback 模型 endpoint 发 HTTP 请求。当前问答代码只从 MediaAgent 进程环境变量 `PICER_LOCAL_MODEL_ENDPOINT` 读取 endpoint，默认 `http://127.0.0.1:8090/api/vl/chat`，IndexService 广播配置不会覆盖它。独立 llama-server 监听的端口并不自动等于 Picer 已配置的 endpoint；只有 endpoint 确实指向 Console/模型服务时，相关服务才是此链路的依赖。
- 2026-10-08：GUI 人脸训练、校准、历史/制品及重启监控已统一经固定公钥的 IndexService → MediaAgent → 本机 ModelServer；GUI 不再依赖 ModelServer 地址或密钥。详见 [P1 实施与验收](P1_IMPLEMENTATION_2026-10-08.md)。

默认本地网关：`http://127.0.0.1:8090`（生产远程地址必须使用 HTTPS）。客户端只保存服务商、模型和 API 根地址；具体模型文件、端口和运行参数不进入 Picer 代码。现有 8000/8001 独立 llama.cpp 进程属于平台内部实现，不能作为 Picer 配置目标。
