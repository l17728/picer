# 搜索性能与测试无输出排查记录（2026-09-28）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

## 结论

本轮没有发现测试代码或搜索代码存在死锁。`Picer.Tests` 使用 `MaxParallelThreads=2`，全量运行时测试排队时间很长；若同时启动多个测试主机，hang 数据收集器会在 60 秒没有新测试行时中止主机，造成“挂起”的假象。ASR multipart、索引重建、网关重试等候选测试均已单独运行通过。全量回归应保证同一时间只有一个 `dotnet test` 进程。

## 搜索优化

`IndexSearchEngine` 在执行文字查询时，只有当云端描述、标签、位置、场景或媒体文字索引中确实有数据时，才加入对应的辅助 `EXISTS` 分支。空辅助索引不再为每一行执行无效相关子查询；FTS5 主查询和计数查询仍保持相同召回语义。

100,000 条记录、30 次选择性查询的实测结果：p50 28.9ms、p95 50.3ms、最大 87.1ms；全库文字压力查询 p50 136.3ms、最大 146.0ms，选择性查询满足 150ms 门禁。

## 回归命令

```powershell
dotnet build picer.sln -c Debug --no-restore --warnaserror
dotnet test tests/Picer.Tests/Picer.Tests.csproj -c Debug --no-build --no-restore --filter "Category!=RuntimeComponent"
dotnet run --project tools/Picer.Benchmarks/Picer.Benchmarks.csproj -c Release --no-restore -- 100000 30
```

全量回归也提供了带心跳和互斥保护的脚本，避免多个测试主机争用同一临时资源而被误判为挂起：

```powershell
powershell -File tools/run-picer-tests.ps1
```

## 最终回归结果

- `Picer.Tests`（`Category!=RuntimeComponent`）：332/332 通过，约 25 分钟；日志：`artifacts/test-logs/picer-tests-20260928-215426.out.log`。
- `Picer.E2E`（`Category!=RuntimeComponent`）：37/37 通过，约 2 分钟；日志：`artifacts/test-logs/picer-tests-20260928-215056.out.log`。
- E2E 收尾修复了 WPF ComboBox 的 UIA 稳定性：模型空间选择不再依赖错误的固定顺序；人物列表更新后重新获取 ComboBox，避免使用旧 Popup 自动化树；人物下拉项增加显式 `AutomationProperties.Name`。

缩略图专项回归覆盖：候选人物代表图与处理后计数（`SuggestedPersonGroup_RemovesProcessedFacesAndUpdatesCount`）、视频关键帧海报加载（`VideoThumbnail_UsesPosterLoaderInsteadOfDecodingVideoFile`）、重复图片检测与内容身份（`DuplicateDetector_*`、`PeopleViews_*`）、远程缩略图/原图中转与缓存复用（`RemoteThumbnailCacheTests`、`GatewayContractTests`）。
