# 远程配置隔离与桌面 E2E 回归（2026-09-22）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
<!-- /historical-status-2026-10-10 -->

## 问题

桌面 E2E 启动真实 `Picer.App.exe` 时只隔离了 `PICER_DATA_ROOT`，但测试进程仍继承开发机的 `backend.json`。当开发机当前配置为远程 IndexService 时，测试应用会错误进入远程模式，导致本地种子数据库中的 `photos` 源目录不显示。

## 修复

- 所有桌面 E2E 进程使用各自临时目录下的 `PICER_BOOTSTRAP_ROOT`。
- 需要进程内初始化 AppServices 的测试同步设置并恢复该变量。
- 测试不再读取开发机的远程端点、令牌或 SPKI 配置；生产应用的本地/远程切换行为不变。

## 验收

- `DirectoryBrowser_SourceAlbumsAndTimelineRootsAlwaysResolvePhotos`：1/1 通过。
- `Picer.E2E` 非 RuntimeComponent：36/36 通过。
- 解决了目录树缺少 `photos` 根节点的真实回归。

完整 `Picer.Tests` 非 RuntimeComponent 套件包含 SQLite 批量写入、索引取消和网络模拟等长耗时测试；单独的 `IndexEngineTests` 19/19 已通过，完整套件仍按发布门禁继续执行，不将超时误报为通过。
