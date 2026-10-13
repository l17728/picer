# 本地/远程 GUI 模式切换（2026-09-21）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
<!-- /historical-status-2026-10-10 -->

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
