# 训练数据质量审计实施记录

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

ModelServer 的 `POST /api/face-training/quality-report` 现在对同一份提交数据执行统一审计，并返回结构化结果：

- train/validation/test 分区和人物覆盖；
- 缺失向量、正样本、排他/未知负样本；
- `GroupId`/图片哈希跨分区泄漏；
- 同一图片哈希对应的重复人脸实例；
- 姿态、光照、表情属性覆盖；
- `warnings` 和 `passed` 门禁结果。

该接口不修改原图、索引或数据集；客户端可在异步训练提交前展示警告，平台仍以不可变评测快照和签名制品作为最终门禁。属性字段缺失时只报告覆盖不足，不会伪造时间或质量信息。

验证：`dotnet build server/Picer.ModelServer/Picer.ModelServer.csproj -c Debug --no-restore --warnaserror` 通过（0 警告、0 错误）。完整单元测试本轮启动后超过既定等待窗口无输出，已中止，需单独定位挂起用例。

同时新增 `tools/verify-platform-services.ps1`，可在 Windows PowerShell 5.1 下检查 ModelServer 和 HTTPS IndexService，支持 IndexService 尚未部署时的 opt-in 允许模式，并输出 JSON 报告。
