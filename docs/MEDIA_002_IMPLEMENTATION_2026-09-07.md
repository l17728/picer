# MEDIA-002：Qwen3-ASR-1.7B 视频转写

视频转写默认配置已使用 Qwen3-ASR-1.7B。请求显式携带 `vad_mode=loose`、`full_audio_fallback=true` 和 `timestamps=true`：VAD 只用于切分和降低静音开销，不会过滤笑声、低音量或短语音；服务端应在分段结果为空/质量不足时执行整段音频兜底并返回时间戳。

音频提取统一为 16 kHz 单声道 PCM WAV。缓存按视频 ID、源文件状态和模型版本复用，旧 Whisper 结果仍可读取，切换模型只生成新缓存，不删除旧结果。UI 运行时按 AI 设置选择 Qwen3-ASR 或 Whisper，并在失败时明确显示失败而不伪装为空转写。
