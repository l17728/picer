# 远程缩略图持久化缓存与空闲预取（2026-09-24）

## 结论

远程 GUI 现在使用 `RemoteThumbnailCache` 将缩略图保存到配置数据目录的
`remote-thumbs` 下。缓存键优先使用原图 `content_sha256`，其次使用 `sha256`，没有哈希时使用经过 SHA-256 规范化的媒体 ID。相同原图在目录、搜索、场景和时间线等多个 UI 视图中只对应一个缓存文件。

## 行为

- 文件采用 `v1-{identity}.jpg` 内容身份键，不覆盖已存在的完整文件。
- 写入使用随机 `.part` 临时文件，完成后原子移动；进程启动时清理残留临时文件。
- 同一进程内相同键使用 single-flight，多个卡片同时请求只产生一次网络下载。
- 进入远程页面后等待约 750ms；用户无操作时，后台依次预取当前 UI 集合中的媒体项，直到当前集合完成或页面切换/控件卸载取消。
- 当前视图切换、目录刷新和搜索会取消旧预取任务；缓存文件保留，之后重新进入直接复用。
- 原图仍使用独立的 `RemoteAssetCache`，缩略图不会与原图缓存混用。

## 验收

- `RemoteThumbnailCacheTests.SameContentSharesOneDownloadAndPersists` 验证并发去重、持久化复用和禁止重复下载。
- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：0 警告、0 错误。
