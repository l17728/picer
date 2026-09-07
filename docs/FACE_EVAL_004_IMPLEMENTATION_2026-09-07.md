# FACE-EVAL-004：跨基模评测矩阵

## 目标

统一评测平台不能绑定 SFace 或任何单一人脸算法。每个算法使用自己的向量模型版本和独立不可变快照，在相同评测分区规则下由同一个指标引擎计算，再生成可比较的结果矩阵。

## 实现

- `FaceAlgorithmEvaluationSpec` 明确算法标识、向量模型版本、快照和策略，禁止以模型名称隐式推断算法。
- `FaceAlgorithmBenchmarkService` 对每个规格调用现有门禁评估，校验算法标识唯一且版本完整。
- 只从通过门禁的结果中推荐算法：先按 FAR 升序，再按 Recall、Precision 降序；全部失败时推荐为空，不会自动替换当前生效版本。
- `FaceAlgorithmBenchmarkResult` 使用版本化 JSON 契约，可供统一模型平台 UI 或远程索引服务消费；不包含原图路径、人物姓名或向量内容。

## 验收

- 跨算法结果必须来自各自快照，不能混用不同向量空间。
- 失败算法不得被推荐；同 FAR 下优先 Recall/Precision 更高者。
- 现有 FACE-EVAL-001/002/REPORT-001 数据和人工归属保持不变。
