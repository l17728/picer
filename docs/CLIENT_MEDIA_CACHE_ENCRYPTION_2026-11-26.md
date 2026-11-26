# GUI 远程媒体缓存静态加密（2026-11-26）

## 本阶段完成

- ServiceMode 的共享 `remote-thumbs` 使用 Windows 当前用户 DPAPI 保护的稳定 256-bit 密钥。密钥存放于远程数据根 `media-cache-key.dpapi`；密钥不写日志，密文缓存以 `PICERENC2` 格式保存。
- 远程下载的缩略图、人脸缩略图和视频关键帧复用同一缓存及密钥。GUI 图片加载、人脸卡片、分类预览、视频关键帧和视频摘要读取关键帧时均通过缓存解密流，不把密文字节交给 WPF 解码器。
- 远程视频摘要拼图单独按同一密钥加密保存，并将旧的明文拼图缓存原子迁移。调用模型时解密到随机命名的临时 JPEG，模型调用完成后立即删除；模型接口目前要求文件路径，因此临时目录在处理期间会有明文。
- 首次启动以后台任务迁移已有 JPEG 明文缓存；读到旧缓存也会先原子写入密文，再把内存中的明文流交给调用者。解密临时数组在流释放时清零。
- 远程视频关键帧从旧 `remote-video-keyframes` 命中时，只有共享缓存完成下载、WPF 解码成功后才删除旧明文文件；失败时保留它以便重试。远程人脸缩略图也沿用成功后清理规则。
- 远程主窗口完成首次加载后即以后台任务扫描旧关键帧目录，不依赖连续镜像同步开关；读取当前空间的视频/关键帧元数据后，按 ID、顺序和差异哈希匹配。可对应的帧迁入共享加密缓存并验证解码后删除旧副本，已不在当前索引中的缓存文件作为陈旧派生缓存清理；元数据未就绪时延后，不处理文件。迁移失败或删除失败时保留文件并记录汇总计数。
- 新下载先写入同目录 `.part` 临时文件，成功后原子发布；下载缓冲限制为 32 MiB，缓存读取受根目录边界约束。
- DPAPI 密钥稳定性、密文下载/读取、旧缓存迁移、媒体密文及视频/人脸因果测试均有自动化覆盖。

## 验证

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：通过，0 警告、0 错误。
- 定向测试（RemoteThumbnailCache、MediaFileCipher、ClientMediaCacheKeyStore、VideoFaceCausal）：25/25 通过。
- 视频摘要策略与并发回归：修复后的策略/并发组合 11/11 通过，加入旧拼图迁移用例后的策略类 8/8 通过，合计覆盖 12 个独立用例；远程缓存/密钥/人脸确认定向回归：25/25 通过。
- E2E 共享媒体资产回归：`SharedImageAssetsTests` 5/5 通过，包含旧视频关键帧迁移到加密共享缓存后删除旧明文副本。
- Debug 全解决方案严格构建：0 警告、0 错误。
- Release：`D:\picer\src\Picer.App\artifacts\builds\client-media-cache-2026-11-26\Release\net8.0-windows\win-x64\Picer.App.exe`，SHA-256 `3251AF66932E18DF48748D4916DFA3C1052259A37672B844AA213F40838D1BD9`，嵌入源码提交 `2490d07`。`dotnet build src/Picer.App/Picer.App.csproj -c Release --no-restore --warnaserror -p:BaseOutputPath=artifacts/builds/client-media-cache-2026-11-26/`：0 警告、0 错误。
- 已关闭前一个 GUI 并从上述 EXE 可见启动。运行 PID `22676`，主窗口句柄非零且 `Responding=True`，标题 `Picer - 图片管理（远程：47-103-99-229.sslip.io:8445）`。CUA 对旧默认路径的窗口归属缓存未能附加截图；Windows 进程路径核验确认实际运行的是上述新构建。
- 视频摘要加密后再次构建：`D:\picer\src\Picer.App\artifacts\builds\remote-summary-cache-2026-11-26\Release\net8.0-windows\win-x64\Picer.App.exe`，SHA-256 `9F7E20E285315343F4FB52DDE6E4E33270CE30A3BEEAA9AEDCD7196FBC4EDCBA`，源码提交 `ccb74c2`，Release 严格构建 0 警告/0 错误。当前 GUI PID `25492` 从此路径运行，窗口响应正常，标题指向 `47-103-99-229.sslip.io:8445`。
- 当前用户远程空间下的 529 个既有视频摘要拼图缓存已启动迁移，日志确认 `migrated=529; failed=0`；抽样磁盘文件头为 `PICERENC2`。DPAPI 密钥文件已存在于空间隔离数据根，文件长度 262 字节。
- 2026-11-26 14:30 关键帧扫尾日志：`migrated=482; discarded=194; failed=0`，当前空间的旧 `remote-video-keyframes` 目录检查为 0 个 JPG、0 字节。初始实现将扫尾附在远程镜像同步之后，导致清理等待长时间冷同步；现已改为主窗口加载后启动一次后台迁移，并在同步完成后重试尚未启动的迁移。
- 严格 Debug 解决方案构建：0 警告、0 错误；E2E `SharedImageAssetsTests`：5/5 通过。最终 Release 构建：`D:\picer\src\Picer.App\artifacts\builds\legacy-keyframe-startup-migration-final-2026-11-26\Release\net8.0-windows\win-x64\Picer.App.exe`；应用宿主 SHA-256 `EE8955B8FAFFB7741A88C20D3457B787263CCBB5ADAFD99B3984F5B17226A052`，实际应用程序集 `Picer.App.dll` SHA-256 `7E34B8A70190C0D3805A24DBE55294A73EE3670DC3AFC4391E9986065FE2D761`。该构建从独立路径启动，运行 PID `22756`，主窗口句柄非零且响应正常，标题指向新服务 `47-103-99-229.sslip.io:8445`。

## 尚未覆盖

- 模型调用期间会在系统临时目录创建明文拼图以满足现有文件路径 API；正常返回、失败或取消后会删除。进程被强制终止或系统崩溃时，临时文件可能残留，后续可将模型输入接口改为受控流以进一步移除这段明文落盘。
- 旧人脸缩略图按需迁移；旧关键帧全量迁移在远程主窗口加载后后台执行，完成情况以 `Legacy remote keyframe cache migration completed` 日志汇总为准。
- ServiceMode 之外的本地原图/索引数据仍按既有本地存储策略管理。
- 设备用户数据目录若被同一 Windows 用户或管理员读取，DPAPI CurrentUser 不提供跨用户隔离之外的保护。
- 这是客户端缓存静态加密，不实现 Relay 长连接、端到端密文透传、零落盘、多租户 Relay 隔离或 Web 管理控制台。RELAY-01 至 RELAY-40 尚未全部实现/验收。
