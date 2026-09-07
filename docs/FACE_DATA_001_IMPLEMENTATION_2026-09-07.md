# FACE-DATA-001：训练数据规模门禁

`FaceTrainingReadinessService` 在训练提交前基于不可变快照检查每个人物正样本（默认至少 10）、排他负样本（默认至少 10）、验证/测试分区、泄漏组数量和至少两位人物。检查结果作为 `FaceTrainingPlan.Warnings` 展示，数据不足时不会伪装成充分覆盖；快照和人工标注不会被修改。
