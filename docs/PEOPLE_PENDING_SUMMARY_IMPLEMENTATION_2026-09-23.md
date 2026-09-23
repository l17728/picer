# 待处理人脸摘要即时同步（2026-09-23）

## 问题

待分类、待确认或待命名人脸完成操作后，右侧列表会先移除，但左侧“待处理人脸”按钮仍显示旧计数，容易让用户误以为操作没有生效。

## 实现

- 操作开始时立即将摘要切换为“正在同步…”，让用户看到操作已被接受。
- 在后台按当前模型空间重新读取 `GetFaceWorkQueueCounts`，更新总数、待确认、待命名和待分类计数。
- 查询延迟 150ms，避开正在提交的事务；后续权威查询不会阻塞 UI。
- 模型空间切换期间丢弃旧查询结果，避免 SFace/InsightFace 的计数串写。
- 记录摘要刷新成功或失败日志，便于定位状态不同步问题。

## 验证

- `dotnet build picer.sln -c Debug --no-restore --warnaserror` 通过（0 警告、0 错误）。
- 已提交并推送：`73b82a7 fix: refresh pending face summary after operations`。
