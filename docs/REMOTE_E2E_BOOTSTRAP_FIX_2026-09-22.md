# 远程配置隔离与桌面 E2E 回归（2026-09-22）

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

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
