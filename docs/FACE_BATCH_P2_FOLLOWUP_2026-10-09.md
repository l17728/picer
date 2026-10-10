# P2 人脸批量推理与验证集诊断

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
> 验收边界：本文训练或人脸准确率数字仅对应当时模型、数据分区和设备，不能代替当前质量门禁；人脸质量等四项旧 P2 验收仍暂停，模型向量保持版本隔离。
<!-- /historical-status-2026-10-10 -->

## 本轮完成

- 修复 `face_worker.py` 将所有裁剪一次性拼成批次的问题。动态批量模型每批最多 16 张；固定批量模型遵守输入声明，最后不足一批时重复末项填充并丢弃填充输出。保留输入顺序及已有向量，不改写调用者样本。
- 已有向量的训练请求直接复用，不再为了阈值计算加载 ONNX 模型。推理返回数量不符、非二维输出、非有限值或零向量时明确失败，避免静默漏样本或生成无效索引。
- 新增 `tools/verify-face-model-batches.py`，使用合成图片逐个加载本机六个候选模型，在 CPU 上验证批量接口。测试不输出人脸向量，不读取真实照片，不写索引。
- 新增 `tools/diagnose-face-validation.py`，只读 artifacts 下隔离数据库，仅查询训练和验证分区，输出汇总诊断；不读取测试分区、不应用模型、不输出人物标识或向量。

## 验证

- `python -m unittest discover -s tests -p face_worker*test.py`：12/12 通过，覆盖动态分批、固定批量、尾批、输出校验、向量复用及指标隔离。
- 真实 ONNX 文件：SFace、InsightFace ArcFace、ArcFace、AdaFace、ArcFace Paddle、FaceX-Zoo 六项 CPU 合成输入检查全部通过。报告 `artifacts/p2-followup-tests/face-model-batches.json`。旧模型导出包含初始化器/输出形状警告，未修改权重或隐藏警告；本检查不验证各模型预处理的识别质量，也不证明 GPU 或低显存验收通过。
- 现有 InsightFace 冻结数据：训练 3,328、验证正样本 963、验证负样本 1,104、8 个人物。无拒识时最近中心身份准确率 93.46%；按人物限制负例接受率的诊断策略，准确率 85.15%、总体 FAR 3.62%、FRR 14.85%，未达门禁。报告 `artifacts/p2-followup-tests/face-validation-diagnosis.json`。
- 上述策略用验证集选阈值，不是独立验收或全局最优阈值证明；不能据此宣称不存在更优策略。它说明这一次按人物收紧阈值仍不足以交付，不应用该策略。
- 本机 ModelServer 健康检查 HTTP 200；排行榜仍为 20 轮、0 个通过候选。报告 `environment-batch-fix.json`。本轮仅修改 Python 和诊断工具，未重新发布 WPF/Linux 程序。
- 本机服务检查时尚未启动 Python 子进程，随后通过真实嵌入接口按现有配置加载本轮 worker；4 个并发合成输入请求全部成功，含首次加载约 7 秒，整卡显存峰值 13,241 MiB。报告 `environment-batch-live.json`；这是短时冒烟，不是持续压力或 2 GB 显存验收。

## 未完成

人脸质量、至少 20 条人工标注真实视频的 ASR 对照、10 分钟真人 AEC3、2 GB 显存目标仍未通过。新增推理兼容性测试不能替代任何质量或设备验收。人工语料清单仍缺失；本轮没有进行新的真人声学录制或低显存硬件测试。
