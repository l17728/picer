# 2026-11-12 Windows 任务栏图标修复

用户反馈新构建 GUI 在 Windows 底部任务栏未显示 Picer 图标。检查发现 EXE 已嵌入 `Assets/Picer.ico`，主窗口也配置了图标；ICO 含 16、24、32、48、64、128、256 像素帧，常用小尺寸帧均有可见像素。新旧候选包从 EXE 提取出的图标相同，因此未重画图标资产。

应用启动时现在设置稳定的 Windows AppUserModelID `Picer.Desktop`，与 GUI 安装包标识一致；主窗口图标改用明确的 WPF 组件资源路径，避免相对路径受启动入口影响。修改仅影响 GUI 的 Shell 身份和图标解析。

`dotnet build src/Picer.App/Picer.App.csproj -c Debug --no-restore --warnaserror` 通过，0 警告/错误。`tools/publish-self-contained.ps1` 的自包含发布及固定资源校验通过，GUI 候选为 `artifacts/release-candidate-20261112/gui-v7-icon`。1191 个文件复制至 `E:/Picer/versions/1.6.5-local-iconfix.20261112`，GUI DLL SHA-256 为 `D4555FA1F2663CC7664979F5582D81A5C992B1BF0B13A499F44EC9433928523E`，复制前后相同。新 EXE 可提取出 Picer 图标。

第一次修改后的 v7 进程 PID 15752 仍在任务栏显示通用窗口图标。系统窗口枚举确认只有一个 Picer GUI，排除了旧 GUI 同时运行；主窗口 `WM_GETICON` 返回的 16/32 像素图标均正确。UI Automation 将任务栏中的通用图标定位到“Picer.App - 1 个运行窗口”。

根据 Microsoft 的 [任务栏图标资源属性说明](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-relaunchiconresource)和[窗口属性存储接口说明](https://learn.microsoft.com/en-us/windows/desktop/api/shellapi/nf-shellapi-shgetpropertystoreforwindow)，在主窗口创建时设置同一 `Picer.Desktop` 标识的重启命令、显示名称及明确的 EXE 图标资源，并使安装包的开始菜单/桌面快捷方式使用相同 AppUserModelID 与图标位置。该窗口属性在 AppUserModelID 之前设置，以触发任务栏刷新。

第二次修复的 v8 自包含发布及校验通过，GUI 位于 `artifacts/release-candidate-20261112/gui-v8-taskbar`。1191 个文件复制到 `E:/Picer/versions/1.6.5-local-taskbar.20261112`，GUI DLL SHA-256 为 `2C3B3A094C1704748B92777456C619417ECF0CCAF23820A9BF69012CC6CFF08D`，复制前后相同。v7 正常退出后启动 v8，PID 17644，主窗口标题为“Picer - 图片管理（远程）”。当前桌面截图 `artifacts/taskbar-v8-crop-20261112.png` 显示任务栏 Picer 按钮已变为蓝色相机图标；修复已在本机现场验证。安装包脚本尚未由 Inno 编译器单独编译。

最终 `dotnet build picer.sln -c Debug --no-restore --warnaserror` 通过，0 警告、0 错误。
