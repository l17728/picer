# FACE-TRAIN-008 GPU Worker 验收记录

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
> 验收边界：本文训练或人脸准确率数字仅对应当时模型、数据分区和设备，不能代替当前质量门禁；人脸质量等四项旧 P2 验收仍暂停，模型向量保持版本隔离。
<!-- /historical-status-2026-10-10 -->

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

## 制品完整性

- ModelServer 对 CPU/GPU 制品统一计算 SHA-256，并使用 ECDSA P-256 签名。
- 客户端下载制品后核对 SHA-256、签名算法、KeyId 和公钥签名；失败时禁止应用人物原型。
- `/api/face-training/signing-key` 提供当前服务实例的公钥和 KeyId。

## 未关闭边界

- 冒烟数据只验证链路，不代表真实人物数据上的模型质量门禁结果。

## 2026-09-15 真实平台复验

- 通过统一模型平台 `http://127.0.0.1:8090/picer` 代理提交 4 个向量样本，任务阶段为 `gpu-worker → gpu-worker-completed`，进度 100%。
- 返回 `prototype-calibration-gpu` 制品；训练准确率和验证准确率均为 `1.0`，FAR/FRR 均为 `0`。
- 返回 ECDSA P-256 签名、KeyId 和 SHA-256；`/algorithms` 与 `/signing-key` 路由均可用。
- 发现并修复旧部署包缺少新签名路由的问题；真实 8091 内部服务已更新，旧 DLL 已备份到 `D:\vl-models\services\Picer.ModelServer.backup-20260915-150211`。
- 指标计算也修正为只使用验证/测试分区计算 `validationAccuracy`，不再把训练集正确数重复累加。
