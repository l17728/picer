# 大照片人脸分析 502 修复（2026-11-25）

## 原因与实现

媒体代理对超过 10 MiB 的原图直接抛出 InvalidDataException，网关将其包装为 502。现场最近五张失败照片实际存在，大小为 17～19 MB；GUI 因连续五次失败停止批次。

FaceImagePayload 对 10～256 MiB 原图在内存生成 JPEG 分析副本，自动处理方向并将长边限制为 4096，必要时继续缩小，直到不超过服务 10 MiB 限制。路径授权检查保持有效，不写入原图。缺失、空文件、超出输入上限和解码失败使用独立错误码。

同时保留并交付先前未提交的远程分析后自动归类修复：有新增分析结果时先同步镜像，再执行归类。

## 验证

- FaceImagePayloadTests：12/12 通过，包括大 BMP 转码后可解码、载荷大小限制、原图字节不变和授权边界。
- MediaAgent Release 构建：0 警告，0 错误。
- 现场 GUI 验证与构建证据见本报告后续记录。

## 范围

本次针对大图导致的 502。模型 worker 无限等待是独立已知问题，本次尚未改动。未运行耗时完整单元套件。

## 现场与正式构建证据

- 功能提交：`accbd7e98cbecfdc71b6e16bbe931c7a1fbb99d8`，已推送 origin/main。
- GUI 实际点击“检查新照片”，进度达到 1754/1754；代理日志确认此前五张超限照片全部 face.detect completed，数据库中相应失败记录已消失。未观察到 GUI 最终汇总文字，不能将进度到顶等同于归类已验收。
- 剩余一条失败记录时间为 2026-11-24 08:06 UTC，早于本次操作，错误类型 InvalidOperationException；本次未清除或伪造其状态。
- GUI、MediaAgent Release 构建均为 0 警告、0 错误；正式 GUI 已重新启动并核对进程路径和窗口。
- GUI：`D:\picer\src\Picer.App\bin\Release\net8.0-windows\win-x64\Picer.App.exe`；业务程序集 `Picer.App.dll` SHA-256：`CDD8F3788562A656E5B5AB3DA91B9BF66D2AF05DC44C2DE713E44081F52A17A4`。
- 代理：`D:\picer\src\Picer.MediaAgent\bin\Release\net8.0-windows\Picer.MediaAgent.exe`；SHA-256：`897FFD0B2CA5891D96FE5122BBBCB250E733BC3D7F4529361A34058D4A85FFF9`。
- 日志：`artifacts/face-tests-final.log`、`artifacts/face-agent-build.log`、`artifacts/face-gui-formal-build.log`。
