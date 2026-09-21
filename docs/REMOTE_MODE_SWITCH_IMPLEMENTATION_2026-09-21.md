# 本地/远程 GUI 模式切换（2026-09-21）

同一个 Picer GUI 现在可以从“文件 → 切换本地/远程模式…”切换索引后端。

- 本地模式：使用本机 SQLite、索引和原图。
- 远程模式：仅填写 IndexService IPv4，程序组装 `https://IP:8445`，读取已有的
  `service-endpoint.json` 完成令牌和 SPKI 校验。
- 保存后程序以 `--wait-for-pid` 安全重启，避免旧进程锁定数据库或继续使用旧 Gateway。
- 远程模式仍禁止访问本机 SQLite；缩略图、搜索和原图通过 Gateway/MediaAgent，原图
  使用本地内容哈希缓存。
- 索引服务状态窗口也提供同一切换入口。

构建验证：`dotnet build picer.sln -c Debug --no-restore --warnaserror` 通过。
