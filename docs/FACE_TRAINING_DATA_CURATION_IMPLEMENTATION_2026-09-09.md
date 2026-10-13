# 人脸训练数据筛选与提交——第一阶段实施报告（增量 1）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
> 验收边界：本文训练或人脸准确率数字仅对应当时模型、数据分区和设备，不能代替当前质量门禁；人脸质量等四项旧 P2 验收仍暂停，模型向量保持版本隔离。
<!-- /historical-status-2026-10-10 -->

日期：2026-09-09
设计依据：[FACE_TRAINING_DATA_CURATION_PROPOSAL_2026-09-04.md](FACE_TRAINING_DATA_CURATION_PROPOSAL_2026-09-04.md)（2026-09-09 用户批准第一阶段）

## 本增量交付（泄漏组 + 不可变快照 + 确定性切分 + 配方预览）

| 项 | 内容 |
|---|---|
| schema v31 | `face_training_snapshots`（规则版本/种子/配比/counts/manifest_hash）、`face_snapshot_members`（face/image/泄漏组/person/split）、`face_snapshot_rejections`（negative_for 对，split 随所属人脸泄漏组） |
| `FaceTrainingSnapshotService` | ① 标签收集：`face_person_assignments(confirmed)`=正样本，`face_person_rejections`=negative_for ② 泄漏组并查集：同图 → 同 `content_sha256` → 同连拍组强制连通 ③ 组级确定性切分：SHA256(规则版本+种子+组) 映射 train/validation/test（70/20/10），全组同 split，杜绝泄漏 ④ 不可变快照：成员/拒绝对一次性写入 + manifest_hash（对排序后规范序列化取 SHA-256）⑤ 资格检查：`CheckEligibility`（≥8 人、每人 ≥5 独立泄漏组、负样本覆盖）——不足时产出警告（降级 prototype-calibration 的依据），不阻断快照构建 |

设计偏离说明：派生表暂以 `face_snapshot_*` 独立存储（不动 `face_instances`）；negative_for 的"每对独立授权"语义已在独立表体现。

## 测试（7 项，`FaceTrainingSnapshotTests`）

同图同组同 split、同内容跨图同组、不同内容独立组、同种子重建 manifest_hash 一致（确定性）、negative_for 继承人脸 split、资格不足产出警告、零确认人脸拒绝构建。

全量回归：`Picer.Tests` 261/261（15 m 31 s，含并行线程池饥饿下扫描作业等待放宽至 120 s）。

## 后续增量（获批范围内的剩余项）

1. 训练提交协议：快照脱敏上传至统一模型平台（复用 `FaceTrainingService`/ModelServer 契约）+ 幂等键 + manifest 哈希校验
2. 三阶段授权 UI：准备/预览 → 冻结并提交 → 应用制品（独立权限点 + 审计）
3. prototype-calibration 强化：多外观原型、按人物自适应边界、质量门槛（`effectiveEmbeddingVersion` 切换与回滚）
4. 低分正样本分层（informative_hard_positive / technical_failure / label_suspect）与 VL 可选辅助分层
