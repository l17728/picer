# 项目状态统一刷新（2026-09-20）

本文件是当前状态的统一索引，优先级高于旧的阶段性实施记录。

## 代码已完成

- 多模型评测快照、排行榜历史、Top 3 保留和 20 轮单模型门禁。
- 训练数据质量审计、缺失向量/泄漏/重复哈希/分区检查。
- Agent 训练状态机：`queued`、`running`、`recoverable`、`completed`、`failed`、`canceled`。
- ModelServer GPU Worker、异步训练、制品签名/哈希、失败恢复。
- HTTPS IndexGateway 的健康、列表和搜索请求重试。
- ModelServer/IndexService 健康验收脚本。
- 10k/100k 选择性搜索性能优化；100k p95 实测 53.2ms。

## 仍需真实环境或数据证据

- 当前真实人脸基线最佳约 64%，尚未达到 90% 应用门禁；排行榜真实训练约 3 轮，尚未达到 20 轮。
- 完整非 RuntimeComponent 测试仍存在长时间无输出用例，需要定位。
- RuntimeComponent 需要完整本机运行时资产。

### 本轮补充

- RuntimeComponent 资产已完成固定大小/SHA-256 校验，真实运行时回归 7/7 通过；详见 `docs/RUNTIME_COMPONENT_REGRESSION_2026-09-20.md`。
- 完整 Picer.Tests 使用 hang-diagnostics 运行时持续输出；已定位为慢测试集合而非挂起。旧 v34 数据库迁移到 v37 的兼容性问题已修复。
- 训练制品达到门禁并自动采用后，人物页会立即刷新当前模型空间、历史制品列表和分类结果，不需要用户手动切换下拉列表。
- 跨机器 HTTPS 的证书/SPKI、鉴权、断线恢复和远程图库联调。
- 真实 GPU 长时训练、Qwen3-VL/ASR/TTS 资源占用及 ASR 对照。
- 真实房间 WebRTC AEC3 十分钟抗回声、插话取消和零误触发。

## 性能状态

- 选择性搜索：10k p95 36.0ms，100k p95 53.2ms，均通过 150ms 门禁。
- 无关键词宽库压力测试仍只作为观测指标，不作为选择性搜索门禁。

## 明确不做

本阶段不实现 ArcFace、AdaFace、InsightFace 等人脸主干神经网络权重微调。训练范围是冻结基模上的向量、原型、阈值和轻量适配策略。
