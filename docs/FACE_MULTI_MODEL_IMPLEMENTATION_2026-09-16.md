# 多模型人脸候选与真实 InsightFace Worker 实施记录

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
> 验收边界：本文训练或人脸准确率数字仅对应当时模型、数据分区和设备，不能代替当前质量门禁；人脸质量等四项旧 P2 验收仍暂停，模型向量保持版本隔离。
<!-- /historical-status-2026-10-10 -->

## 2026-09-17 部署收口

ArcFace、AdaFace、ArcFace-Paddle（MobileFace-Paddle 128 维）和 FaceX-Zoo ResNet50-IR 已完成真实权重转换、哈希记录和 ONNX Runtime 验收。统一平台现由 Local Model Console `8090` 代理，Picer ModelServer 后端监听 `8091`；四个模型均已通过 `/picer/api/face-training/embeddings` 端到端请求。Worker 在未显式设置环境变量时使用 D 盘已校验模型路径，避免平台启动脚本与直连脚本产生不同的可用模型集合。

## 本次完成

- ModelServer 保留算法中立的候选适配器、场景路由、加权共识和 Top 3 排名策略。
- InsightFace ArcFace 使用 `buffalo_l/w600k_r50.onnx` 实际加载，GPU Worker 优先使用 CUDAExecutionProvider，失败时回退 CPU。
- `FaceSample` 支持 `faceImageBase64`。当模型为 `insightface-arcface` 且客户端没有该模型向量时，Worker 对 112×112 人脸裁剪图实时生成 512 维向量；其他模型仍要求显式 embedding，避免模型向量混用。
- 数据集接口对图像输入、向量维度、泄漏分组和模型版本分别校验；训练任务仍异步执行并签名归档制品。
- 客户端新增并行训练协议：每个模型单独读取历史记忆、生成配方、执行最多 9 轮并独立记录失败；一个模型失败不会取消其他模型。UI 会先显示平台实际 ready 模型数量。

## 实机验收

- 本地 ModelServer：`http://127.0.0.1:8090`，`/health` 返回 `ok`。
- `/api/face-training/algorithms` 显示 SFace 与 InsightFace 可用；其余候选只有在配置对应模型文件后才标记可用，不伪造可用状态。
- 以两个 `faceImageBase64` 样本提交 InsightFace 数据集并创建训练任务，GPU Worker 完成并返回签名 artifact，状态由 `queued → running/gpu-worker → completed`。

## 当前边界（2026-09-18 更新）

ArcFace、AdaFace、ArcFace-Paddle、FaceX-Zoo、SFace 和 InsightFace 均已进入统一候选协议，并已使用同一份平台评估快照完成基线评估。GUI 快照发布器会从索引人脸框生成受控的 112×112 裁剪图和模型所需输入；不兼容或未通过哈希校验的权重不会进入可用集合。

当前剩余的是训练质量和环境门禁，而不是 GUI 到 Worker 的接口阻塞：排行榜共完成 3 轮，Top 3 为 InsightFace-ArcFace、AdaFace、ArcFace；ArcFace-Paddle、FaceX-Zoo、SFace 暂列备用。基线最佳验证准确率约 64.36%，需要真实标注数据和后续迭代才能判断是否达到 90% 激活门禁。
## 2026-09-20 状态修订

多模型评测、排行榜历史和 Top 3 策略已完成代码实现；当前约 3 轮真实训练，最佳真实基线约 64%，尚未达到 20 轮单模型决策和 90% 应用门禁。
