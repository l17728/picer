# 人脸模型排行榜实现记录（2026-09-17）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
> 验收边界：本文训练或人脸准确率数字仅对应当时模型、数据分区和设备，不能代替当前质量门禁；人脸质量等四项旧 P2 验收仍暂停，模型向量保持版本隔离。
<!-- /historical-status-2026-10-10 -->

训练平台新增多模型排行榜，所有成绩均来自已签名训练制品的验证指标，不接受客户端直接写入分数。

## 接口

- `POST /api/face-training/model-rankings/baseline`：提交包含 `faceImageBase64` 的评测快照，平台为当前所有可用模型并行创建训练/评测任务，并使用同一个 `evaluationId` 归为一轮。
- `GET /api/face-training/model-rankings`：返回当前排名、综合成绩、最佳验证准确率、FAR/FRR、轮数、通过轮数、夺冠次数和 Top-3 保留状态。
- `GET /api/face-training/model-rankings/rounds`：返回每轮每个模型的名次、分数、制品和门禁结果。

## 计算规则

单轮分数为 `ValidationAccuracy - FAR - 0.25 × FRR`。同一 `evaluationId` 下按分数、验证准确率和模型名稳定排序；首位计为该轮冠军。综合成绩为该模型所有轮次分数的平均值，排行榜优先比较综合成绩，再比较夺冠次数和最佳验证准确率。累计达到 20 轮后，平台保留综合排名前三的模型。

普通训练任务也会自动生成 `EvaluationId`（快照 + attempt），因此后续 GUI 提交会自然进入同一排行榜体系。历史 JSONL 记录可向后兼容读取。
