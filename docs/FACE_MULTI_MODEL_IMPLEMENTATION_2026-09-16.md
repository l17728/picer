# 多模型人脸候选与真实 InsightFace Worker 实施记录

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

## 当前边界

ArcFace、AdaFace、ArcFace-Paddle、FaceX-Zoo 的适配器已纳入统一候选协议，但未将不兼容的 PyTorch/Paddle 权重强行转换成 ONNX；没有经过哈希校验的权重不会进入可用集合。客户端若要使用 InsightFace 图像推理，应提交已对齐的人脸裁剪图（不提交原图路径）。当前 GUI 快照仍只产生 SFace 向量，因此 InsightFace 在 GUI 端的唯一剩余阻塞是从索引人脸框生成 112×112 裁剪并提交，不是服务端模型或 GPU Worker 阻塞。
