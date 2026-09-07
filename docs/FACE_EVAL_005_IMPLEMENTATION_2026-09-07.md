# FACE-EVAL-005：远程评测制品交换

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
