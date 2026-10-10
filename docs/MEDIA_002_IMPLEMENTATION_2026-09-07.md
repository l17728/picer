# MEDIA-002：Qwen3-ASR-1.7B 视频转写

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

视频转写默认配置已使用 Qwen3-ASR-1.7B。请求显式携带 `vad_mode=loose`、`full_audio_fallback=true` 和 `timestamps=true`：VAD 只用于切分和降低静音开销，不会过滤笑声、低音量或短语音；服务端应在分段结果为空/质量不足时执行整段音频兜底并返回时间戳。

音频提取统一为 16 kHz 单声道 PCM WAV。缓存按视频 ID、源文件状态和模型版本复用，旧 Whisper 结果仍可读取，切换模型只生成新缓存，不删除旧结果。UI 运行时按 AI 设置选择 Qwen3-ASR 或 Whisper，并在失败时明确显示失败而不伪装为空转写。
