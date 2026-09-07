# PLATFORM-BENCH-001：模型端点基准

新增 `EndpointBenchmarkService`，对任意模型端点执行 1–20 次可取消请求并记录成功数、失败类型、P50/P95 延迟。该工具不上传媒体内容，适合在 Qwen3-ASR、Qwen3-VL、中文 TTS 和训练平台实际启动后进行冷/热启动及性能对照；真实 GPU/显存数据仍需平台运行时采集。
