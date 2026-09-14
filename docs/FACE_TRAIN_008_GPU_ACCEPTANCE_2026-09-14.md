# FACE-TRAIN-008 GPU Worker 验收记录

## 环境

- GPU：NVIDIA GeForce RTX 4060 Ti 16GB
- Python：3.12.10
- PyTorch：2.6.0+cu124
- CUDA Runtime：12.4

## 验收结果

- `torch.cuda.is_available()` 返回 `True`。
- 2048×2048 CUDA 张量矩阵计算完成。
- ModelServer API 完成数据集提交、异步任务创建、状态轮询和制品下载。
- GPU Worker 返回 `prototype-calibration-gpu` 制品，测试数据包含 4 个样本、2 个人物，任务状态达到 100%。

## 联调中修复

- Worker 同时兼容 C# 默认序列化的 PascalCase 与 camelCase 字段。
- Worker 非零退出时，ModelServer 会从 stdout 或 stderr 提取错误，不再返回空失败原因。

## 未关闭边界

- 当前完成真实 GPU 训练与评估联调；服务端非对称制品签名仍需单独完成。
- 冒烟数据只验证链路，不代表真实人物数据上的模型质量门禁结果。
