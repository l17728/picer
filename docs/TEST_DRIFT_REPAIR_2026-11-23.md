# 测试漂移修复与持久预览能力协商实现报告（2026-11-23）

## 背景

`main` 上存在 6 项既有失败（2 项单元 + 4 项 E2E），经 `git stash` 干净基线复验均为确定性失败，与运行机器无关。此前在 [TAG_ALBUM_SYNC_IMPLEMENTATION_2026-11-20.md](TAG_ALBUM_SYNC_IMPLEMENTATION_2026-11-20.md) 中误记为“本机环境问题、CI 为绿”，该表述未经核实，已订正。本报告记录三项根因、修复方式，以及修复过程中发现的一个被掩盖的产品缺陷。

## 根因与修复

### 1. 持久预览协议缺少能力位（`01bdd40`，2026-11-18 引入）

- **现象**：`MediaStreamingTests` 两项在缩略图请求上等满 30 秒 `HttpClient.Timeout`；服务端日志为 8 个请求全部 499，且无任何代理回连。
- **根因**：缩略图改为先走 `media.preview`（等待上限 2 分钟），真实 MediaAgent 已实现，但测试替身 `TestAgent` 未同步，不应答该消息。更重要的是协议本身没有能力位：服务端只校验 `media.binary.v1`，无法识别不支持 `media.preview` 的旧驻留程序，[MEDIA_CHANNEL_REUSE_2026-11-18.md](MEDIA_CHANNEL_REUSE_2026-11-18.md) 承诺的“旧版驻留程序保留 fallback”实际不成立——新版服务搭配 11-18 之前的代理时，每个缩略图都会先空等 2 分钟。
- **修复**：采用能力协商而非版本号比对（沿用现有机制，代理与服务独立部署时只有能力位能回答“这台代理会不会这个功能”）。
  - `MediaWire.PreviewCapability = "media.preview.v1"`；MediaAgent `BuildCapabilities()` 上报；`AgentConnectionRegistry.RequestPreviewAsync` 仅在能力存在时走持久预览，否则立即回退到预览媒体通道。
  - `TestAgent` 实现 `media.preview` 应答（含失败应答，与真实代理一致），并增加 `persistentPreview` 参数模拟旧程序。
  - 两个测试改为双变体：新代理（1 次预览请求、0 条媒体连接）与旧代理（0 次预览请求、1 条预览通道连接），两条路径均保持覆盖。
- **兼容性**：新服务 + 旧代理 → 立即回退；旧服务 + 新代理 → 旧服务不认该能力位，照旧发送，新代理能处理。纯增量。

### 2. E2E 断言了从未发布的 UI（`0e486e7`，2026-11-17 引入）

- `MainWindow_ExposesCoreNavigationAndSearchToUiAutomation` 期待菜单项“语义索引管理与诊断”。已提交的 `MainWindow.xaml` 在全部历史中均为“中文语义检索”（`0e486e7` 对该文件的唯一改动是添加 BOM），[USER_GUIDE.md](USER_GUIDE.md) 亦如此；前者只是其打开的 `ClipSemanticWindow` 的窗口标题。测试改为匹配已发布且已写入用户指南的“中文语义检索”。
- `ClassificationV2UiTests` 期待 `ClassificationV2Window` 及导航开关、启用状态等控件。该名称的 XAML 从未被提交；`ClassificationV2Client` 的唯一使用者是 `ClassificationBrowserWindow`（标题“照片分类与审核”），即 V2 分类界面的最终形态（同日经三次恢复提交定名，见 [ENGINEERING_MEMORY.md](ENGINEERING_MEMORY.md)）。测试按真实窗口重写，保留原意：独立入口可达、分类树与照片列表存在、筛选开关可切换、“增量扫描”只生成计划且 `classification_v2_jobs` 保持为 0（无模型调用）。
- 结论：改测试匹配应用，不恢复从未发布的 UI。若希望菜单改名为“语义索引管理与诊断”，需同时修改 `MainWindow.xaml` 与用户指南，属独立的产品决定。

### 3. 本地模式关闭连续同步后未跟进（`afefd2d`，2026-11-19 引入），及被掩盖的产品缺陷

- **测试过时**：`LocalAccountSynchronizationTests` 在本地模式下期待通知基线非空、并靠通知自动下载照片空间里的修改。按 [LOCAL_INDEX_RELAY_SDD_2026-11-19.md](LOCAL_INDEX_RELAY_SDD_2026-11-19.md) §3/§6，本地索引模式为权威、连续同步必须关闭。测试改为断言 `ContinuousSyncEnabled == false`、基线为 null、直接写入空间的修改**不会**在本地出现；测试更名为 `LocalModePublishesThroughItsOwnPairedAccountWithoutContinuousSync`。
- **被掩盖的缺陷**：通知循环 `RunSpaceNotificationsAsync` 同时承担“连续双向同步”和“空间巡检的本机执行与回执”。`afefd2d` 在循环入口直接返回，把两件事一起关掉，但巡检窗口未改动——本地模式下仍可点击巡检、任务仍会创建，本机却永远不执行，界面停在“离线来源等待上线”。该问题此前被第 116 行的早期失败掩盖。
- **修复**：将巡检执行与连续同步解耦。
  - `StartSpaceNotifications` 增加 `forInspection`；仅当连续同步开启或为巡检而启动时才建立监听。
  - 循环内，变更通知在连续同步关闭时只确认、不触碰任何一端的索引（`source_not_registered` 的自动重注册同样受此约束）；巡检通知照常执行。
  - `IndexRepairWindow` 在创建巡检任务前启动监听，任务不再活跃或窗口关闭时停止；重开窗口发现上次任务仍在进行时恢复监听。本地模式不再因此产生常驻连接。

## 验证

- 严格构建 `dotnet build picer.sln -c Debug --no-restore --warnaserror`：0 警告 0 错误。
- 定向单元测试：`MediaStreamingTests` + `AgentConnectionRegistryTests` 34/34；`SpaceInspectionClientTests` + `SpaceSyncReceiverTests` + `IndexBackendConfigTests` 48/48。
- 定向 E2E：三组原失败用例全部通过；`LocalAccountSynchronizationTests` 由超时（3–5 分钟）恢复为约 21 秒，多来源的冲突与目录退休流程一并跑通。
- 完整 E2E 套件（`tools/run-picer-tests.ps1`）：171/171 通过，7.7 分钟。
- 完整单元套件（同 runner）：1241/1241 通过，2.5 小时，零失败——修复前为 1239 项中 2 项失败。

## 边界与说明

- 本地模式无常驻通知连接的直接后果：由**其他设备**发起的空间巡检，仅在本机巡检窗口打开期间才会被本机执行。这是“本地索引模式不保持持续连接”的应有结果，如需改变须先修订设计文档。
- 巡检进行中关闭巡检窗口会停止监听，未完成的任务需重新发起。
- CI 的实际状态未能直接读取（本机 `gh` 未登录）。上述 6 项按代码逻辑为确定性失败，且未被任何标签排除，`ci.yml` 会执行它们；请在 Actions 页面确认 `main` 自 11-17 起的实际状态。
