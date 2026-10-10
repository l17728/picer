# 统一 Service 能力实施记录（2026-09-28）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

## 本次完成

- MediaAgent 使用单实例互斥（已有全局命名 Mutex），向 IndexService 注册真实能力，不再把未实现的 FFmpeg、ASR、TTS、人脸能力伪装成可用。
- 增加 `task.open`/`task.result`/`task.error` WebSocket 协议及服务端请求等待机制。
- 增加 `GET /api/service/v1/video-probe/{videoId}`：IndexService 根据索引中的视频 ID 解析原始路径，通过驻留 MediaAgent 执行本机 `ffprobe`，GUI 不可提交任意路径或直接调用本机进程。
- 增加 `ffmpeg.audio_extract` 任务和 `GET /api/service/v1/video-audio/{videoId}`；MediaAgent 以单声道 16 kHz WAV 抽取音频，限制输出 32 MiB，并在任务结束后清理临时文件。
- 增加 `asr.transcribe` 任务和 `POST /api/service/v1/video-transcribe/{videoId}`；MediaAgent 在源机抽取完整音轨后调用 `PICER_LOCAL_ASR_ENDPOINT`，支持模型、宽松 VAD、整段回退和时间戳参数，远程 GUI 不接触 ASR 端点。
- 增加 `ffmpeg.keyframes` 任务和 `POST /api/service/v1/video-keyframes/{videoId}`；关键帧写入源视频旁的 `.picer-keyframes` 受控缓存，不修改原视频，返回时间戳与路径清单。
- 增加 `tts.synthesize` 任务和 `POST /api/service/v1/tts`；文本只在 Service → MediaAgent → `PICER_LOCAL_TTS_ENDPOINT` 链路中转，远程 GUI 不直连 TTS。
- 增加 `vl.analyze` 任务和 `POST /api/service/v1/vl`；VL 能力只有在 MediaAgent 注册了实际 `PICER_LOCAL_VL_ENDPOINT` 后才显示 `ready`，避免“Agent 在线但模型不可用”的假状态。
- 增加 `face.detect`、`face.embed`、`face.calibrate`、`face.train` 的 Service 任务协议及 `POST /api/service/v1/face/{operation}`；该 API 能经 MediaAgent 转发到 `PICER_LOCAL_FACE_ENDPOINT`。代码复核发现 GUI 的训练/校准入口仍通过 `PICER_MODEL_SERVER_URL` 直接调用 ModelServer，尚未切换到该任务 API；不可据此认为客户端训练路径已统一。
- 增加 `GET /api/service/v1/capabilities` 的 Agent 实际能力状态；能力只有在 Agent 注册并具备对应处理器时才显示 `ready`。
- 远程 GUI 的模型配置和本地直连入口已禁用；AI 流式请求继续通过 IndexService → MediaAgent 转发。

## 验证

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：通过，0 警告、0 错误。
- 视频索引定向测试此前通过；能力状态契约测试此前通过。
- RuntimeComponent 回归已完成（Picer.Tests 2/2、Picer.E2E 7/7）；完整非 RuntimeComponent 套件仍有既有长耗时用例，未将其超时误报为通过，按测试类隔离运行。

## 收尾状态（2026-09-28）

FFmpeg 关键帧、音频抽取、ASR、TTS、VL、Face detect/embed 及视频任务已有 Service task 路由；流式 AI 问答走 IndexService → MediaAgent，MediaAgent 默认向 `127.0.0.1:8090/api/vl/chat` 发起请求。人脸训练 GUI 入口仍直连 ModelServer，且 AI 问答 endpoint 目前由 MediaAgent 环境变量控制、未由 IndexService 广播动态切换，因此服务路由并未完全收口。流式 TTS/VL 自动化验收与 GPU Worker 验收不代表这两项架构差异已解决。
