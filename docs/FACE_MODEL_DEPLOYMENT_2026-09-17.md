# 多模型人脸候选部署记录（2026-09-17）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
> 验收边界：本文训练或人脸准确率数字仅对应当时模型、数据分区和设备，不能代替当前质量门禁；人脸质量等四项旧 P2 验收仍暂停，模型向量保持版本隔离。
<!-- /historical-status-2026-10-10 -->

统一模型平台已完成六个候选模型的本地部署与端点验收。模型文件位于 `D:\VL-models\models\candidates`，不进入 Git 仓库；服务启动脚本通过环境变量引用它们。

| 模型 | 文件 | SHA-256 | 输出维度 | 验收 |
|---|---|---|---:|---|
| ArcFace | `arcface.onnx` | `FFE014A45C9488506719D37FD578ECE6661BB385535B36E8039975FA5D4683DB` | 512 | ONNX Runtime + `/api/face-training/embeddings` |
| AdaFace | `adaface_ir_18.onnx` | `6B6A35772FB636CDD4FA86520C1A259D0C41472A76F70F802B351837A00D9870` | 512 | ONNX Runtime + API |
| ArcFace-Paddle（MobileFace-Paddle 推理权重） | `arcface_paddle.onnx` | `C642B9C3EF2F8295425D5CF9C55E8CA07C4BCFA3F7557B1DD70E1EB9BB88B375` | 128 | Paddle 推理包转换 ONNX + API |
| FaceX-Zoo ResNet50-IR | `facex_zoo.onnx` | `8E0C9C8329578CF956A926D66B988204C831232410BB0F355523FE461687901B` | 512 | PyTorch checkpoint 转 ONNX + API |

已有 InsightFace ArcFace 和 SFace 基线也保持可用。六个模型均由 `/api/face-training/algorithms` 报告 `available=true`，并使用同一个 `/api/face-training/embeddings` 协议；输入布局和空间尺寸由 ONNX 模型签名动态读取，禁止跨模型复用向量。

FaceX-Zoo 转换脚本为 `tools/convert_facex_zoo.py`。外部模型文件缺失或 SHA-256 不匹配时，适配器会报告不可用，不会静默回退到其他模型。
