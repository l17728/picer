# GPU Worker 常驻进程改造

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
<!-- /historical-status-2026-10-10 -->

## 背景

训练服务此前每个任务都创建一个新的 Python/CUDA 进程。单次任务可以完成，但连续任务会反复初始化 CUDA，并可能在进程退出阶段无输出或超时。

## 改造

- `GpuTrainingWorkerHost` 在 ModelServer 内以单例运行；
- Python Worker 使用已有的 `--server` 行协议，启动一次后复用 CUDA 上下文；
- 通过 `SemaphoreSlim` 串行化同一 Worker 的请求，避免 JSON 响应交叉；
- 每个任务仍有独立超时；超时、空响应、进程退出会杀掉并重启 Worker；
- 下一任务自动使用新进程，不会复用损坏的 CUDA 状态；
- GPU Worker 未配置时仍保留原有 CPU 训练路径。

## 验证

`Picer.ModelServer` Debug + `--warnaserror` 构建通过。真实 GPU 长时验收仍需在新服务进程部署后执行多任务、取消和失败恢复测试。
