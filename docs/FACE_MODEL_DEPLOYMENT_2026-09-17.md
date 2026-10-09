# 多模型人脸候选部署记录（2026-09-17）

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
> 验收边界：本文训练或人脸准确率数字仅对应当时模型、数据分区和设备，不能代替当前质量门禁；人脸质量等四项旧 P2 验收仍暂停，模型向量保持版本隔离。
<!-- /historical-status-2026-10-09 -->

统一模型平台已完成六个候选模型的本地部署与端点验收。模型文件位于 `D:\VL-models\models\candidates`，不进入 Git 仓库；服务启动脚本通过环境变量引用它们。

| 模型 | 文件 | SHA-256 | 输出维度 | 验收 |
|---|---|---|---:|---|
| ArcFace | `arcface.onnx` | `FFE014A45C9488506719D37FD578ECE6661BB385535B36E8039975FA5D4683DB` | 512 | ONNX Runtime + `/api/face-training/embeddings` |
| AdaFace | `adaface_ir_18.onnx` | `6B6A35772FB636CDD4FA86520C1A259D0C41472A76F70F802B351837A00D9870` | 512 | ONNX Runtime + API |
| ArcFace-Paddle（MobileFace-Paddle 推理权重） | `arcface_paddle.onnx` | `C642B9C3EF2F8295425D5CF9C55E8CA07C4BCFA3F7557B1DD70E1EB9BB88B375` | 128 | Paddle 推理包转换 ONNX + API |
| FaceX-Zoo ResNet50-IR | `facex_zoo.onnx` | `8E0C9C8329578CF956A926D66B988204C831232410BB0F355523FE461687901B` | 512 | PyTorch checkpoint 转 ONNX + API |

已有 InsightFace ArcFace 和 SFace 基线也保持可用。六个模型均由 `/api/face-training/algorithms` 报告 `available=true`，并使用同一个 `/api/face-training/embeddings` 协议；输入布局和空间尺寸由 ONNX 模型签名动态读取，禁止跨模型复用向量。

FaceX-Zoo 转换脚本为 `tools/convert_facex_zoo.py`。外部模型文件缺失或 SHA-256 不匹配时，适配器会报告不可用，不会静默回退到其他模型。
