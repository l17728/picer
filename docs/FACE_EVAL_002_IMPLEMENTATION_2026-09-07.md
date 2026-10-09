# FACE-EVAL-002 实施记录

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
> 验收边界：本文训练或人脸准确率数字仅对应当时模型、数据分区和设备，不能代替当前质量门禁；人脸质量等四项旧 P2 验收仍暂停，模型向量保持版本隔离。
<!-- /historical-status-2026-10-09 -->

## 算法无关边界

评测快照不再把 SFace 当作管线类型：`algorithm_id`、`embedding_model_version` 和 `adapter_version` 是独立元数据。评测策略通过 `FaceEvaluationPolicy` 注入原型和阈值，因此同一评测器可比较不同基模、不同适配器和不同阈值策略。SFace 只是当前默认配置，不是评测器依赖。

UI 与索引的边界由 Core 服务和数据契约定义；UI 不直接拼接索引 SQL。后续索引可由本地 SQLite 或 HTTPS 数据源实现，必须返回相同的快照/成员契约，更新通过版本化 API 完成。

## 指标与门禁

`FaceEvaluationService` 对冻结快照的 validation/test 分区计算：

- 人物级及总体 Precision、Recall、FAR、FRR；
- 未知/排他负样本拒识率；
- `small-face`、`low-confidence`、`standard` 困难分组；同时支持外部人工困难标签映射（侧脸、小脸、逆光、儿童、旧照片等）；
- `Compare` 对比基线和候选策略，FAR 不得增加，FRR/Recall/拒识率按门限比较，任何困难分组劣化都会阻止应用。

每次评测写入 `face_evaluation_runs` 和 `face_evaluation_metrics`，不修改人脸归属、原型或索引。
