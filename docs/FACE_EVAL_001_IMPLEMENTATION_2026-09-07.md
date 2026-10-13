# FACE-EVAL-001 实施记录

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
> 验收边界：本文训练或人脸准确率数字仅对应当时模型、数据分区和设备，不能代替当前质量门禁；人脸质量等四项旧 P2 验收仍暂停，模型向量保持版本隔离。
<!-- /historical-status-2026-10-10 -->

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
