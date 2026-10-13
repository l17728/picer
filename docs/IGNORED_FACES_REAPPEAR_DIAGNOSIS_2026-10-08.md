# 14 张不分类人脸反复出现的定位（2026-10-08）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
<!-- /historical-status-2026-10-10 -->

## 结论

人工“不分类”操作曾成功写入本地镜像库，但上传失败；启动同步使用服务端旧快照整表替换本地人脸工作流，丢失尚未上传成功的人工标记。当前没有持久化待上传操作或冲突合并机制保护这些修改，因此重复点击只能暂时从界面移除。

本次完成定位，未修改人脸分类数据，也未实施同步协议修复。

后续修复及实际数据恢复见 [人工状态同步修复报告](FACE_REVIEW_SYNC_FIX_2026-10-08.md)。

## 实际证据

- 只读查询 `remote-mirror.db`：14 个不同 face ID，各有 5 次 `ignore` 审计记录，共 70 条。最近一批操作时间为 2026-09-30 06:40:35 UTC。
- 同一数据库中，这 14 个 face ID 当前都缺少 `face_review_states` 状态。
- 直接使用 `VisionAnalysisRepository.GetUnassignedFaces` 的实际 SQL 查询 InsightFace 空间，返回恰好 14 张，与上述审计集合完全相同，排除其他人脸或单纯显示计数问题。
- 只读查询服务端数据库：这 14 条检测记录全部存在，但对应 review 状态为 0 条。
- 2026-09-30 14:40:35 本机日志记录开始同步“人脸标记不分类”；14:50:56 记录上传失败：`Error while copying content to a stream.`。
- 后续服务端镜像快照持续返回 10423 条 review 状态，而不包含这 14 条人工标记。

## 代码链路

1. `PeopleViewModel.IgnoreFacesAsync` 在当前数据库提交并复核 `ignored`，随后调用 `QueueImmediateRemoteSync` 异步上传。界面成功提示只代表本地提交，不代表远程持久化。
2. `IndexSyncClient.PushMetadataAsync` 在上传工作流元数据前，先上传全量人脸记录及向量；这些大请求的失败会阻止后续 review 状态上传。
3. `App.RemoteMirrorLoopAsync` 在 service 模式启动时先执行远程镜像拉取，再执行本地增量发布。
4. `AppServices.SyncRemoteMirrorCoreAsync` 调用 `PicerDatabase.ReplaceMirroredFaceWorkflow`；后者执行 `DELETE FROM face_review_states`，再插入远程快照。未同步的本地人工状态因此被抹除。
5. 服务端元数据接收路径也使用整表工作流替换，多个来源的旧快照覆盖属于额外风险；当前证据足以确认本机上传失败与启动覆盖，不将多客户端覆盖断言为本次已证实事件。

## 修复应覆盖的范围

- 将人工分类操作与派生候选快照分开，持久化待上传操作，服务端确认前不得因拉取旧快照而丢失。
- 人工“不分类/恢复分类”使用可幂等重放的增量操作及版本或冲突规则，避免仅将 ignored 集合永久取并集而破坏恢复分类。
- 小型人工状态提交不应依赖全量图片、向量上传成功；界面明确区分本地保存和同步完成。
- 回归需覆盖上传失败、关闭并重启、旧快照拉取、重试成功、恢复分类，以及旧发布端再次推送快照。

## 旧程序清理

已删除确认缺少配套 DLL 的孤立文件 `src/Picer.App/bin/Debug/net8.0-windows/Picer.MediaAgent.exe`（2026-09-24，151552 字节）。完整新版代理及原始媒体未删除。
