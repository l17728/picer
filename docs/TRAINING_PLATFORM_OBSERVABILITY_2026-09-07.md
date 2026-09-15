# 训练平台可观测性联调

Picer 训练客户端现在会在任务状态轮询期间读取服务端返回的 `logsUrl`，将最新日志行通过进度回调显示到训练窗口；日志读取失败不会阻断状态轮询。

训练制品响应支持 `sha256`、`signature` 和 `signatureAlgorithm` 字段。客户端始终校验 SHA-256；若服务端提供签名，则校验算法标识，未知算法直接拒绝应用。当前统一平台通过 `/picer/api/face-training` 代理内部 ModelServer，已实际返回 ECDSA P-256 公钥、KeyId、SHA-256 和签名，并完成 GPU Worker 联调。

任务取消、状态轮询和制品门禁均保持旧版本模型不变，失败日志只用于诊断，不会改变人工归属或索引。
