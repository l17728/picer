# FACE-EVAL-001 实施记录

## 目标

建立可复现、不可变且无同源泄漏的人脸训练/验证/测试数据快照。

## 已实现

- 数据库 schema v27 增加 `face_dataset_snapshots` 与 `face_dataset_members`。
- 快照保存随机种子、数据版本、SFace 模型版本、adapter 版本及分区计数。
- 同一原图的 SHA-256、连拍组成员和同一视频的全部关键帧使用同一 `leakage_group`，分区由 `seed + group` 的 SHA-256 稳定计算，避免跨 train/validation/test 泄漏。
- 快照成员保存人脸/视频人脸 ID、正负标签、分区、质量分数、向量及向量哈希；创建后通过数据库触发器禁止更新和删除。
- `FaceTrainingService.Prepare` 和提交前确认均创建/复用同一快照，快照 ID 变化时拒绝提交，避免人工标注变更造成训练错位。
- `Get`/`GetMembers` 提供审计和回归验证入口。

## 验收

- 相同输入与随机种子产生相同快照 ID。
- 同一泄漏组只出现在一个分区。
- 快照表更新/删除会被数据库拒绝。
- 已加入 `FaceEvaluationSnapshot_IsImmutableAndLeakageSafe` 回归用例。

## 边界

本阶段只冻结数据和分区，不训练 SFace 主干；FACE-EVAL-002 负责指标计算与新旧策略门禁。
