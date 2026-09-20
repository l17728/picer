# 性能与多模型评测回归报告（2026-09-20）

## 多模型排行榜

- 排行榜数据来自同一不可变评测快照。
- 每次模型评测记录 `EvaluationId`、模型版本、轮次、指标、制品和时间。
- `/api/face-training/model-rankings/rounds` 返回完整历史；未达到 20 轮时保留 Top 3，达到 20 轮后才允许单模型决策。
- 未测量模型显示“暂无数据”，不会伪装成 0 分。

## 搜索基准

| 数据量 | 迭代 | p50 | p95 | 门禁 |
|---:|---:|---:|---:|---|
| 10,000 | 20 | 28.4 ms | 36.0 ms | 通过（<150 ms） |
| 100,000 | 20 | 184.7 ms | 234.7 ms | 未通过（目标<150 ms） |

基准命令：

```powershell
dotnet run --project tools/Picer.Benchmarks/Picer.Benchmarks.csproj -- 10000 20
dotnet run --project tools/Picer.Benchmarks/Picer.Benchmarks.csproj -- 100000 20
```

取消路径已有 SQLite command cancel 和流水线取消测试；100k p95 性能缺口仍保留为发布前 P0 优化任务，不通过放宽门禁掩盖。
