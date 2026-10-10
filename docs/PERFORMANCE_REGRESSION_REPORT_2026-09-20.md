# 性能与多模型评测回归报告（2026-09-20）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

## 多模型排行榜

- 排行榜数据来自同一不可变评测快照。
- 每次模型评测记录 `EvaluationId`、模型版本、轮次、指标、制品和时间。
- `/api/face-training/model-rankings/rounds` 返回完整历史；未达到 20 轮时保留 Top 3，达到 20 轮后才允许单模型决策。
- 未测量模型显示“暂无数据”，不会伪装成 0 分。

## 搜索基准

| 数据量 | 迭代 | p50 | p95 | 门禁 |
|---:|---:|---:|---:|---|
| 10,000 | 20 | 28.4 ms | 36.0 ms | 通过（<150 ms） |
| 100,000 | 10 | 19.6 ms | 53.2 ms | 通过（<150 ms） |

基准命令：

```powershell
dotnet run --project tools/Picer.Benchmarks/Picer.Benchmarks.csproj -- 10000 20
dotnet run --project tools/Picer.Benchmarks/Picer.Benchmarks.csproj -- 100000 20
```

优化措施：当辅助文字表没有数据时，检索只生成 FTS5 主条件，避免对每张图片执行空的场景/位置/标签子查询；同时增加不带 `status` 前缀的有效时间索引，覆盖 IncludeMissing 的宽检索。取消路径已有 SQLite command cancel 和流水线取消测试。
