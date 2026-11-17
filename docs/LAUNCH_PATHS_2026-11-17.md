# Picer 启动路径记录

## 当前唯一入口

- GUI：`D:\picer\src\Picer.App\bin\Release\net8.0-windows\win-x64\Picer.App.exe`
- 驻留程序：`D:\picer\src\Picer.MediaAgent\bin\Release\net8.0-windows\Picer.MediaAgent.exe`
- GUI 快捷方式：用户桌面和开始菜单中的 `Picer 图库.lnk`
- 登录启动项：`Picer 媒体驻留代理.lnk`，指向上面的 Release 驻留程序

## 运行时要求

当前程序为 .NET 8 Windows Desktop 应用，系统已验证安装 `Microsoft.WindowsDesktop.App 8.0.33`。启动时应使用上述快捷方式或 Release 路径，避免旧构建目录触发错误的运行时安装提示。

## 已清理

已删除 `E:\Picer\versions\` 下 10 个历史本地版本目录，共回收约 14.7 GiB。旧启动项已替换为当前 Release 驻留程序；后续构建完成后只更新上述两个入口。
