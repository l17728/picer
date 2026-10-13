# FACE-EVAL-005：远程评测制品交换

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
> 验收边界：本文训练或人脸准确率数字仅对应当时模型、数据分区和设备，不能代替当前质量门禁；人脸质量等四项旧 P2 验收仍暂停，模型向量保持版本隔离。
<!-- /historical-status-2026-10-10 -->

## 目标

Picer UI 不直接依赖本地 SQLite 或具体人脸算法。评测结果可发布到统一平台或远程索引服务，平台通过稳定的版本化契约读取；原图、裁剪图、向量和人物真实姓名不会通过此接口发送。

## 契约

- `POST /api/face-evaluation/v1/artifacts` 发布 `FaceEvaluationArtifactEnvelope`。
- `GET /api/face-evaluation/v1/artifacts/{artifactId}` 读取制品。
- 请求可携带 Bearer Token；服务端负责鉴权和租户隔离。
- 制品包含 `artifactId`、SHA-256 和跨算法评测结果；客户端对响应重新计算哈希，校验失败立即拒绝。
- 远程地址必须 HTTPS；仅 `localhost/127.0.0.1` 允许 HTTP，便于本机开发服务。
- 传输支持取消、超时和 HTTP 错误传播；远程不可用时不改变本地索引、人工归属或生效模型。

## 解耦边界

`FaceEvaluationRemoteClient` 只依赖 `FaceAlgorithmBenchmarkResult` 契约，不依赖 SFace、YuNet 或 SQLite。未来索引服务迁移到 HTTPS 时，UI 只替换契约实现，不重建目录树或改变本地数据结构。
