# GUI 远程媒体缓存静态加密（2026-11-26）

## 本阶段完成

- ServiceMode 的共享 `remote-thumbs` 使用 Windows 当前用户 DPAPI 保护的稳定 256-bit 密钥。密钥存放于远程数据根 `media-cache-key.dpapi`；密钥不写日志，密文缓存以 `PICERENC2` 格式保存。
- 远程下载的缩略图、人脸缩略图和视频关键帧复用同一缓存及密钥。GUI 图片加载、人脸卡片、分类预览、视频关键帧和视频摘要读取关键帧时均通过缓存解密流，不把密文字节交给 WPF 解码器。
- 首次启动以后台任务迁移已有 JPEG 明文缓存；读到旧缓存也会先原子写入密文，再把内存中的明文流交给调用者。解密临时数组在流释放时清零。
- 新下载先写入同目录 `.part` 临时文件，成功后原子发布；下载缓冲限制为 32 MiB，缓存读取受根目录边界约束。
- DPAPI 密钥稳定性、密文下载/读取、旧缓存迁移、媒体密文及视频/人脸因果测试均有自动化覆盖。

## 验证

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：通过，0 警告、0 错误。
- 定向测试（RemoteThumbnailCache、MediaFileCipher、ClientMediaCacheKeyStore、VideoFaceCausal）：25/25 通过。
- Release：`D:\picer\src\Picer.App\artifacts\builds\client-media-cache-2026-11-26\Release\net8.0-windows\win-x64\Picer.App.exe`，SHA-256 `3251AF66932E18DF48748D4916DFA3C1052259A37672B844AA213F40838D1BD9`，嵌入源码提交 `2490d07`。`dotnet build src/Picer.App/Picer.App.csproj -c Release --no-restore --warnaserror -p:BaseOutputPath=artifacts/builds/client-media-cache-2026-11-26/`：0 警告、0 错误。
- 已关闭前一个 GUI 并从上述 EXE 可见启动。运行 PID `22676`，主窗口句柄非零且 `Responding=True`，标题 `Picer - 图片管理（远程：47-103-99-229.sslip.io:8445）`。CUA 对旧默认路径的窗口归属缓存未能附加截图；Windows 进程路径核验确认实际运行的是上述新构建。

## 尚未覆盖

- `remote-video-summary-cache` 中已生成的整张拼图文件仍可能是明文；本阶段仅确保拼图读取的关键帧经过加密共享缓存解密。
- ServiceMode 之外的本地原图/索引数据仍按既有本地存储策略管理。
- 设备用户数据目录若被同一 Windows 用户或管理员读取，DPAPI CurrentUser 不提供跨用户隔离之外的保护。
- 这是客户端缓存静态加密，不实现 Relay 长连接、端到端密文透传、零落盘、多租户 Relay 隔离或 Web 管理控制台。RELAY-01 至 RELAY-40 尚未全部实现/验收。
