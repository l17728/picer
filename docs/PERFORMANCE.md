# Picer 性能基准

在同一台机器、相同电源模式和空闲负载下运行：

```powershell
dotnet run --project tools/Picer.Benchmarks -c Release -- 1000 30
dotnet run --project tools/Picer.Benchmarks -c Release -- 100000 30
dotnet run --project tools/Picer.Benchmarks -c Release -- 1000000 30
```

第三个参数可指定数据卷，例如 `-- 100000 30 D:\bench-root`；工具只在该目录下创建并清理自己的 `picer-benchmark-*` 子目录，用于 SSD/HDD 对照。

工具以批量方式生成混合日期、尺寸、相机及目录的本地 SQLite 清单；生成吞吐仅代表合成数据准备速度，不代表真实图片解码索引吞吐。门禁搜索使用约 1% 选择性的目录词并输出 p50/p95/max；匹配全库的查询作为独立压力值报告，不混入典型 p95。报告还包含工作集、分配量和各代 GC 次数。SSD/HDD 测试应把应用数据目录分别放在对应卷；WebDAV 使用 0/100 ms RTT 和 HTTP 500、超时、无权限故障注入。

报告需记录：日期、提交版本、CPU、内存、存储、Windows/.NET 版本、数据量、运行参数、三次结果及中位数。UI 验收另记录首屏时间、连续滚动 60 秒的丢帧和峰值工作集。
