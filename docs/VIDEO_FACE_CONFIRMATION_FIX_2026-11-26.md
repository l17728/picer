# 视频与照片人物确认失效修复（2026-11-26）

## 问题

- 视频人物卡片确认后仍可能带有旧模型推荐；服务端镜像快照重放人工状态时也没有清除推荐字段，界面容易继续呈现旧推荐，错误身份纠正看起来没有生效。
- 切换视频期间，WPF 可能暂留旧视频的人脸选择，工具栏操作存在使用过期选择的风险。
- 照片建议卡在其他操作已撤回建议后仍留在虚拟化列表时，“是”按钮原先静默返回，没有刷新提示。

## 修复

- 视频人工确认在本地写入及因果状态重放时，均清空 `suggested_person_id` 和 `match_confidence`。目标人物与确认状态通过 `video-face-state` 继续同步；旧服务器快照重放后不会复活已确认的人物建议。
- 切换视频时清除旧人脸选择；工具栏命令只处理当前视频列表内仍可见的人脸。
- 照片确认卡发现其建议已变化时，立即隐藏旧按钮、刷新列表并显示状态；刷新失败会记录错误并向用户说明。

## 验证

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：通过，0 警告、0 错误。
- `dotnet test tests/Picer.Tests/Picer.Tests.csproj -c Debug --no-build --no-restore --filter "FullyQualifiedName~VideoFaceCausalTests|FullyQualifiedName~FaceMetadataCausalTests|FullyQualifiedName~MediaFileCipherTests"`：25/25 通过。
- 视频回归测试覆盖已确认人物纠正、清除旧建议、以及旧镜像快照重放后仍保留人工确认。
- 正式配置 Release GUI：commit `97fb946594902b995590391f029597f0eef8988d`；路径 `src/Picer.App/artifacts/builds/video-face-confirmation-2026-11-26/Release/net8.0-windows/win-x64/Picer.App.exe`；SHA-256 `DD47711C8539536885DC9E30B3FD84370266EC197A8CFA12E2303B29157DB030`。

修复构建已通过 Release 侧载方式启动并显示；进程 PID 3296，窗口标题为 `Picer - 图片管理（远程：47-103-99-229.sslip.io:8445）`，窗口句柄非零。正式 GUI 原路径未被覆盖；本次修复不需要部署 IndexService。Debug 验证程序集：`src/Picer.App/bin/Debug/net8.0-windows/Picer.App.dll`。
