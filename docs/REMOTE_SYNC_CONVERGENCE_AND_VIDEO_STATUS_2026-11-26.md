# 远程索引收敛与视频分析状态修复（2026-11-26）

## 结论与同步边界

同步方向由索引权威方决定，不把两个数据库当作可任意合并的对等主库：

- **服务器索引模式**：服务器是权威索引。GUI 将本地镜像产生的授权修改推送到服务器，并从服务器读取变更更新镜像；远程浏览直接查询服务器。短暂离线期间不承诺立刻一致，网络恢复且服务可用后，持久化待办和重试机制继续收敛。待处理冲突必须保留，不能覆盖权威数据。
- **本地索引模式（服务器只作 Relay）**：本地数据库是唯一权威索引。持续索引同步、空间变更通知和远程镜像重试均关闭；Relay 转发请求和媒体流，不触发索引上传或下载。切换索引权威方时仍执行设计要求的单次迁移同步。

“最终一致”是有条件的：进程能够重试、网络和授权最终恢复、持久化队列仍可读且没有待解决冲突时，镜像才会收敛。永久断网、凭据撤销、服务器故障或无法自动解决的冲突不能被诚实地承诺为自动收敛。远程 GUI 浏览不依赖本地镜像是否追平。

## 服务器索引模式的失败恢复

- `SyncRemoteMirrorAsync` 的同步失败会写入应用数据根目录下的 `remote-mirror-reconciliation.pending`（默认 `%APPDATA%\picer`，也受 `PICER_DATA_ROOT`/数据位置配置影响），并在后台持续重试；间隔从 5 秒指数增长，最大 5 分钟。
- 严格同步把待发布描述、元数据编辑、人脸审核状态、分类偏好、标签、相册、人物及媒体增量都作为成功门槛。任何阶段失败都不把镜像标成完整。
- 成功完成同步后才清除待办标记。GUI 退出时取消 worker；重启后冷启动完成会检查标记并恢复。重复执行依赖现有 outbox、operation ID 和 revision checkpoint 保持幂等。
- 远程人脸分析每 25 个成功写入或 30 秒安排一次非阻塞镜像刷新；结束、取消或异常时做一次有界刷新。仍失败时写持久待办并进入上述重试路径。
- 本地索引模式不创建该标记，也不启动该重试路径。`IndexBackendConfig.Load` 对本地模式强制 `ContinuousSync=false`，现有 `IndexBackendConfigTests` 覆盖此约束。

## 视频页分析状态丢失的根因与修复

远程视频目录改为直接从 IndexService 分页读取后，GUI 曾用 `VideoItemViewModel(video, 0)` 构造每行数据；这丢掉了原先本地 `GetFaceReviewSummaries()` 和关键帧计数提供的状态，因此“分析状态”列可能空白。失败状态仅保存在客户端镜像，也未必能在服务器目录列表中显示。

现在每页响应携带该页视频的关键帧数、人脸统计、人物分析状态、是否有转写和摘要。GUI 用这些值恢复状态列，并将失败状态明确显示为“人物失败”。IndexService 的远程视频人物分析失败路径会把失败状态写入服务器索引；GUI 在失败后重载当前行，立刻展示服务端状态。成功状态由服务端事务与检测结果一同提交。

分页状态通过批量查询获取，不对每个视频额外请求。较旧 IndexService 忽略新增 JSON 字段时，GUI 仍能从 `VideoRecord.PersonAnalysisStatus` 显示人物完成状态，但关键帧/转写/摘要小标记需在 IndexService 升级后恢复。

## 验证记录

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：0 警告、0 错误。
- `GuiReadOptimizationTests`、`FaceReviewSyncTests`、`IndexBackendConfigTests`：25/25 通过。
- `VideoDetailsRegressionTests`、`PeopleAnalysisCompletionTests`、`RemoteMirrorReconciliationTests`：18/18 通过。
- 新增测试验证 HTTP 视频分页同时返回关键帧数、人脸分析状态和转写标记；另验证分析失败状态可出现在视频列表状态列。
- GUI Release：`src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe`，SHA-256 `D1EBCE528F00C0C3A70F4C811B47E12F75F54E37BE67275316B97596060F095A`；构建 0 警告、0 错误。
- IndexService Release 本地程序集：`server/Picer.IndexService/bin/Release/net8.0/Picer.IndexService.dll`，SHA-256 `FAD5DE36018AF7CDCA0C5988D3E09E27FABB7EFA61E79928D7244738A9484577`；构建 0 警告、0 错误。Linux x64 部署结果待执行后补记。
