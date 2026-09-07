# PLATFORM-HEALTH-001：模型服务健康探针

新增 `ModelServiceProbe`，可对训练平台、Qwen3-ASR、Qwen3-VL 和中文 TTS 端点执行轻量 GET 探测，返回连通性、HTTPS 安全状态、延迟和错误类型。远程 HTTP 端点直接判定不安全；本机回环地址允许 HTTP 便于开发。探针只读，不上传照片、音频或向量，可接入诊断中心作为真实服务启动验收前置检查。
