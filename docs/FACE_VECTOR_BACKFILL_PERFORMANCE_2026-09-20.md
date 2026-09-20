# 人脸向量补齐性能改造记录（2026-09-20）

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
