# 本地/远程 GUI 模式切换（2026-09-21）

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

同一个 Picer GUI 现在可以从“文件 → 切换本地/远程模式…”切换索引后端。

- 本地模式：使用本机 SQLite、索引和原图。
- 远程模式：仅填写 IndexService IPv4，程序组装 `https://IP:8445`，读取已有的
  `service-endpoint.json` 完成令牌和 SPKI 校验。
- 保存后程序以 `--wait-for-pid` 安全重启，避免旧进程锁定数据库或继续使用旧 Gateway。
- 远程模式启动与本地相同的 `MainWindow.xaml` 原始主界面；远程图库窗口作为其远程内容入口。
- 远程模式仍禁止访问本机 SQLite；缩略图、搜索和原图通过 Gateway/MediaAgent，原图
  使用本地内容哈希缓存。
- 索引服务状态窗口也提供同一切换入口。

构建验证：`dotnet build picer.sln -c Debug --no-restore --warnaserror` 通过。
