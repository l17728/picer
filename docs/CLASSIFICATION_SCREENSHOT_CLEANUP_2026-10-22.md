# 截图分类清理与分类预览修复（2026-10-22）

## 背景

视觉概念目录曾将“屏幕截图”作为独立自动分类，混淆了文件/媒介形式与照片内容、场景分类。分类审核窗口在远程模式下还会把服务端缩略图路径当成本机路径，导致照片预览为空。

## 实施

- 从视觉概念词库和 CLIP 零样本候选中移除 `document.screenshot`，新分类不会继续产生该标签。
- 分类窗口打开时，清理仍有效的旧 `document.screenshot` 分类记录（包括已确认的旧标签），并删除兼容表中的旧概念行。分类断言状态改为 `retracted`，审计行保留；用户已明确忽略的 `rejected` 记录不恢复。
- 对受影响照片重新投影现有元数据与已完成的照片描述标签。不会为此启动新的图像分析，也不会读取、修改原始照片。
- 远程分类预览改用远程缩略图 API 和内容寻址缓存，列表滚动到项目时才下载；本地模式沿用本地缩略图/原图预览。

## 验证

- `git diff --check` 通过。
- `dotnet build src/Picer.App/Picer.App.csproj -c Debug --no-restore --warnaserror` 通过，0 警告、0 错误。
- `dotnet build server/Picer.IndexService/Picer.IndexService.csproj -c Debug --no-restore --warnaserror` 通过，0 警告、0 错误。
- `dotnet test tests/Picer.Tests/Picer.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~ClassificationCleanupTests"` 通过，2 项。
- 整体 solution 构建被正在运行的 MediaAgent 锁定其输出 DLL；受影响的 GUI、IndexService 分别完成构建。
