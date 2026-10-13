# 人脸向量补齐性能改造记录（2026-09-20）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
> 验收边界：本文训练或人脸准确率数字仅对应当时模型、数据分区和设备，不能代替当前质量门禁；人脸质量等四项旧 P2 验收仍暂停，模型向量保持版本隔离。
<!-- /historical-status-2026-10-10 -->

本次只改造现有服务和核心库，没有创建或启动新的 GUI 应用。

## 已实施

- ModelServer 的 GPU embedding worker 改为常驻进程协议，复用 Python 进程和 ONNX Runtime Session。
- Worker 对一个请求中的裁剪图执行批量推理，避免逐张 `session.run`。
- 客户端默认批次从 16 提升到 64，可通过 `PICER_FACE_VECTOR_BATCH_SIZE` 在 16～128 之间调整。
- 同一批次内相同原图只读取一次，避免连拍照片重复磁盘读取。
- 人脸裁剪使用受控 CPU 并发，可通过 `PICER_FACE_CROP_PARALLELISM` 调整。
- 数据库写入仍保持串行事务，避免 SQLite 嵌套事务和锁竞争。

## 并发边界

模型推理默认单常驻 Worker，避免多个 ONNX/CUDA Session 争抢显存。若后续确认显存余量，再增加 Worker 池；当前 P3 已实现 CPU 裁剪并行和流水线前的安全边界，未盲目复制 GPU 模型。

## 验证

- `face_worker.py` 通过 Python 语法检查。
- `Picer.ModelServer` 和 `Picer.Core` 在 Debug、`--warnaserror` 下编译通过。
