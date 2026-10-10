# 人脸训练 Agent 工具化架构 SDD

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
> 验收边界：本文训练或人脸准确率数字仅对应当时模型、数据分区和设备，不能代替当前质量门禁；人脸质量等四项旧 P2 验收仍暂停，模型向量保持版本隔离。
<!-- /historical-status-2026-10-10 -->

日期：2026-09-15  
状态：评审中，评审通过后实施

## 1. 目标

将人物页和人脸精度验证窗口统一到同一个训练 Agent 核心。Agent 根据历史实验、人工操作、时间分布、连拍去重、困难样本和模型评测结果构建训练配方，平台异步训练并以 90% 验证准确率、FAR≤1% 作为应用门禁。

## 2. 三层责任边界

### 2.1 原则层

原则是版本化、不可被 Agent 修改的训练宪法：

- 同一原图、内容副本、连拍组和视频关键帧不得跨训练/验证/测试分区。
- 不同人脸模型版本的向量不得混用。
- 连拍和高度相似内容必须去重，避免单一时刻支配配方。
- 样本必须覆盖不同年份、季节、设备和困难姿态；时间只用于分层，不作为身份依据。
- 人工确认优先于自动建议；失败制品不得应用。
- 原始媒体和人工标注不可被训练流程修改或删除。

### 2.2 规则代码层

规则代码是强制门禁，不接受 Agent 绕过：快照冻结、泄漏分组、哈希/连拍去重、最低样本量、版本一致性、签名和 SHA-256、Precision/Recall/FAR/FRR、未知人物拒识率、制品状态机和回滚。

### 2.3 Agent 工具层

Agent 只负责解释证据、选择工具、组合流程和提出下一轮配方。所有写操作只能通过有 schema、有权限和有审计的工具完成；Agent 不得直接写 SQLite、文件或模型制品。

## 3. 工具语义全集

工具按读、计算、提交、应用四类权限划分：

### 3.1 数据盘点

`get_labeled_people`、`get_face_sample_counts`、`get_rejected_faces`、`get_action_logs`、`get_media_statistics`、`get_model_versions`、`get_missing_embeddings`。

### 3.2 数据质量

`detect_duplicate_faces`、`detect_duplicate_content`、`detect_burst_groups`、`detect_same_source_leakage`、`inspect_embedding_quality`、`inspect_temporal_coverage`、`inspect_person_imbalance`、`inspect_pose_lighting_occlusion`。

### 3.3 配方构建

`build_baseline_recipe`、`build_temporal_balanced_recipe`、`build_quality_balanced_recipe`、`build_hard_positive_recipe`、`build_exclusive_negative_recipe`、`build_burst_deduplicated_recipe`、`build_action_guided_recipe`、`compare_recipes`、`get_recipe_fingerprint`。

每个配方必须返回样本指纹、选中/排除数、人物平衡、时间覆盖、去重数、正负比例、原因和警告。

### 3.4 评测与诊断

`run_threshold_sweep`、`calculate_far_frr`、`calculate_precision_recall`、`calculate_unknown_rejection`、`calculate_difficult_group_metrics`、`diagnose_threshold_reachability`、`diagnose_embedding_separability`、`diagnose_data_insufficiency`。

诊断结果必须二选一：`calibrate-threshold` 或 `upgrade-model`。阈值扫描在所有安全门限下仍低于 90% 时，禁止继续低频调阈值。

### 3.5 基模评测

`list_face_algorithms`、`prepare_algorithm_embeddings`、`evaluate_algorithm_candidate`、`compare_algorithm_candidates`、`select_best_algorithm`、`check_model_resource_budget`。候选模型必须使用同一不可变快照和同一分组规则比较。

### 3.6 训练任务

`submit_training_job`、`get_training_status`、`get_training_logs`、`cancel_training_job`、`resume_failed_training`、`get_worker_capability`、`check_gpu_memory`。任务必须幂等，按数据集指纹去重，支持断点和失败恢复。

### 3.7 制品管理

`get_artifact`、`verify_artifact_signature`、`verify_artifact_hash`、`compare_artifact_to_baseline`、`check_quality_gate`、`stage_artifact_for_review`、`apply_artifact`、`rollback_artifact`。

### 3.8 记忆与审计

`get_recent_attempts`、`get_best_attempt`、`get_failed_attempt_reasons`、`get_recipe_history`、`summarize_training_memory`、`record_training_decision`、`record_failure_analysis`。

## 4. 状态机

`DataInsufficient → DataDiagnosed → RecipeBuilt → Submitted → Running → Evaluated → GateChecked → Staged → Applied`。

异常状态为 `BlockedByLeakage`、`BlockedByVersion`、`NeedsModelUpgrade`、`FailedRecoverable`、`FailedTerminal`、`Canceled`。只有 `Staged` 经用户确认后才能进入 `Applied`。

## 5. Agent 决策策略

1. 读取历史和当前数据质量。
2. 先检查时间覆盖、连拍/内容去重和泄漏。
3. 构建基线、时间均衡和困难样本候选配方。
4. 比较配方指纹，重复配方直接跳过。
5. 先做阈值扫描；可达 90% 时校准阈值。
6. 不可达 90% 时诊断基模能力，进入候选模型并行评测。
7. 训练、验证、签名、门禁、用户确认和应用。
8. 把本轮证据、失败原因和配方写入历史，供下一轮使用。

## 6. 数据与时间策略

时间用于泛化能力评估而非身份判断。每个人物优先覆盖多个年份/季节/设备；同一连拍组只保留代表样本；验证集应包含与训练集不同时间段的样本。完全相同内容、连拍相似内容和同一视频关键帧必须进入同一泄漏组。

## 7. 模型策略

当前 SFace 训练入口是原型/阈值适配，不修改 SFace 神经网络权重。若阈值扫描在安全 FAR 条件下仍无法达到 90%，Agent 必须停止重复调参，提交候选基模评测任务。候选模型之间按同一快照、同一指标和同一资源预算比较，最佳制品才可进入应用审批。

## 8. 可观测性与隐私

记录任务 ID、快照 ID、模型版本、配方指纹、样本统计、指标、诊断、工具调用和决策原因；不记录原始路径、人物姓名、问题文本或向量内容。远程调用必须显式授权，所有制品必须可验证、可回滚。

## 9. 测试与门禁

- 单元：规则、去重、时间分层、阈值诊断、配方指纹、工具 schema。
- 集成：客户端→平台→Worker→制品→校验→暂存。
- 回归：重复配方跳过、泄漏阻断、版本不匹配、签名/哈希错误、GPU 失败恢复、回滚。
- 评测：人物级 Precision/Recall/FAR/FRR、未知拒识、正脸/侧脸/小脸/逆光/儿童/旧照片分组。
- 应用门禁：验证准确率 ≥90%、FAR≤1%、无严重回归，且用户确认制品。
## 10. Review gates (2026-09-15)

Three independent reviews (training architecture, ML evaluation, and Agent/tool platform) approved the three-layer direction and required the following implementation gates:

1. Principles, hard rules, and tools are separately versioned and hashed. Principles are read-only; hard rules are enforced in code; the Agent composes registered tools only.
2. Each run has an immutable `TrainingRun` parent and multiple `Attempt` children. Snapshot, effective model version, recipe fingerprint, grouping policy, and random seed are fixed and auditable.
3. Every tool definition includes `inputSchema`, `outputSchema`, `readOnly`, `requiredScopes`, idempotency mode, preconditions, side effects, timeout, retry policy, and audit event type. Results include trace ID, snapshot ID, policy version, evidence, warnings, and stable error codes.
4. Evidence, planning, execution, and artifact tools are separately authorized. `apply_artifact` requires explicit user authorization and cannot be called automatically by the Agent.
5. The server revalidates snapshot immutability, source/burst/video leakage groups, embedding version and dimension, counts, and recipe fingerprint; client declarations are not trusted.
6. Evaluation uses train/calibration/validation/frozen-test partitions. Threshold tuning never uses frozen test. Reports include person-level Precision/Recall/FAR/FRR, unknown rejection, macro and worst-person metrics, difficult-condition groups, and confidence intervals.
7. Negative semantics are explicit (`negativeForPersonId`, unknown, and not-classified). “Not classified” is never silently treated as an exclusive negative for a person.
8. Repeated idempotency keys return the existing dataset/job. Server restart recovery, cancellation, retry classification, and checkpoint behavior are observable.

Implemented in the current Agent: adaptive nine-attempt recipe fingerprints, temporal/burst/content-dedup strategy inputs, training history and action-log evidence, train/validation isolation, negative exclusion from positive prototypes, threshold-reachability diagnosis, and the 90% validation/FAR quality gate with an `upgrade-model` stop signal. Not yet claimed complete: a formal Tool Registry, persistent server event state machine, parallel multi-model weight training, and automatic population of capture/burst/content metadata from the database. These remain explicit follow-up gates; free-form text must not replace them.

## 11. Tool state and recovery

The required state chain is `DISCOVER -> AUDIT_DATA -> BUILD_RECIPE -> CHECK_RECIPE_DUPLICATE -> EVALUATE_THRESHOLD -> DIAGNOSE -> SELECT_MODEL_OR_ADAPTER -> SUBMIT -> MONITOR -> VERIFY_ARTIFACT -> QUALITY_GATE -> STAGE_FOR_REVIEW -> USER_APPROVAL -> APPLY/ROLLBACK`. Every transition is an immutable audit event. The idempotency key is `hash(snapshotId + effectiveModelVersion + recipeFingerprint + attempt + pipelineVersion)`. Leakage, version mismatch, and signature errors are terminal; only transient worker/network faults are retryable.

## 12. Multi-model strategy

The platform is algorithm-agnostic: a run may benchmark several registered adapters (for example SFace, ArcFace and a future local model) against the same immutable snapshot, leakage groups, split manifest and metrics contract. Each adapter gets an independent embedding-version namespace; vectors and thresholds are never mixed. The Agent first attempts safe threshold/recipe calibration. If the diagnostic reports capacity limits, it schedules parallel candidate evaluation subject to GPU-memory and time budgets, compares macro/worst-person/open-set metrics and confidence intervals, and only stages the best candidate. This is a benchmark-and-selection path; it must not be confused with claiming that the current prototype-calibration worker has changed SFace network weights.

For personal non-commercial use, all locally deployable candidates may participate: SFace, ArcFace, InsightFace ArcFace, AdaFace, ArcFace-Paddle and FaceX-Zoo-derived adapters. Each completed nine-iteration Agent run is one model round. The ranking memory retains validation accuracy, FAR, FRR, gate result, artifact and scenario. Before 20 measured rounds all candidates remain eligible; after 20 rounds only the Top 3 are retained. The Agent may then explore a weighted consensus ensemble of the retained models. Weights are learned only from frozen validation data, while the final test partition remains untouched; model vectors are never mixed before model-specific scoring.

Runtime decision policy is `scenario route -> calibrated weighted score -> agreement vote -> accept/review/reject`. A majority agreement accepts only when the weighted score also passes the calibrated threshold. A disagreement is routed to manual review rather than forced into a person; unanimous low scores are rejected as unknown. Scenario-specific ensembles are queried through `/api/face-training/model-ensemble?scenario=...`, and the auditable decision endpoint is `/api/face-training/model-ensemble/decide`.
