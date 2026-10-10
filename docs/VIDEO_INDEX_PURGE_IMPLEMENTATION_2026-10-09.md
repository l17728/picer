# 视频索引手动彻底清除（2026-10-09）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
> 后续衔接：清理已区分本地和远端镜像缓存，非末引用也清独立海报、下载与失败暂存；共享帧由存活数据库引用保护。末引用同时清理历史生成目录、内容编辑及操作绑定、扫描清单和本地哈希缓存；源文件仍不删除。
<!-- /historical-status-2026-10-10 -->

视频页提供“彻底清除视频索引”。用户确认后，删除所选路径的索引引用及该引用的任务请求、持久化结果、任务日志、下载和摘要缓存；原视频文件保留。相同内容仍有其他路径引用时，共享转写、摘要、人物和关键帧保留，末次引用删除才由内容外键级联清除。

本地、HTTP `POST /api/gateway/v1/videos/{id}/purge` 和命名管道 `purge_video` 使用同一语义。服务端先取消并等待该视频的分析请求，再清理 ServiceTaskRunner 文件与 TaskJournal 记录。独立 `service-tasks.sqlite` 增加 `task_media` 关联表，将新产生的代理子任务关联到物理视频引用；删除后执行 WAL checkpoint 和 VACUUM。历史代理日志只有任务类型，没有视频身份，不能追溯关联，但其中没有媒体路径或分析正文。

`VideoOperationLifetime` 在进程内按源路径协调摘要、FFmpeg、远程关键帧下载与清除：清除先取消并等待写入者，在清除期间拒绝新写入。源主机 `video.purge-cache` 同样等待本机任务退出，仅接受授权根目录下的 `.picer-keyframes/<64位散列>` 缓存；FFmpeg 取消会终止进程树并等待退出。代理不可用或缓存清理失败时保留索引用于重试，不宣称完成。

缓存删除检查路径边界和重解析点，禁止借符号链接进入其他位置。已保存关键帧路径只有落入已知应用缓存目录时才有资格清除。独立人脸人物目录不因视频删除而清除，照片人脸缓存也不触碰。

照片和视频清除同时失效目录跟踪器的内存状态及持久 USN 清单，移除路径、文件标识和散列缓存，下次显式扫描重新建立清单。代理端也清除自身清单。跨路径共享关键帧使用源路径和实际缓存目录两层任务租约，最后一个引用即使位于其他目录，也等待旧位置的关键帧写入任务退出。

清除同时删除 `mirror_owned_media` 本地镜像归属行。服务端 `mirror_media_changes` 保留仅含媒体类型、数值 ID、源 ID、版本号的删除协议标记，让其他客户端获知删除；标记不含路径、散列或描述正文，也不参与内容检索。

定向测试源码：`VideoPurgeTests.PurgeKeepsSharedEvidenceUntilLastReferenceAndNeverDeletesOriginals`、`PurgeCancelsAndWaitsForCacheWriterAndRejectsNewWriterUntilFinished`、`JournalPurgeRemovesLinkedAgentRequestsButKeepsUnrelatedTasks`。统一构建及执行由主审查流程完成，本实现提交时尚未执行。

补充测试覆盖：`PurgeRemovesOwnedSourceCacheButPreservesNearbyOriginalAndUnownedRecordedFile` 验证源旁缓存边界；`ServiceTaskStorageTests.VideoPurgeRemovesCompletedReplayPayloadResultAndJournalAcrossRestart` 验证请求、结果和重试数据在重启后不再可用；`VideoPurgeWaitsForCanceledHandlerAndErasesItsLateResult` 验证在途任务等待和阻止回填；WPF STA 命令测试 `PurgeCommandRequiresConfirmationRemovesIndexAndKeepsOriginal` 验证用户确认、忙状态、刷新和原文件保留。

## 补充修复：缓存代际与部分重试

本地与远程镜像使用明确的缓存范围，避免同一数据目录中不同数据库的相同数值 ID 相互清除缓存。非末引用立即清除其独立下载、海报及失败抽帧目录；仍被其他引用或其他内容记录引用的关键帧目录保留，末引用通过实际记录路径清理旧引用目录。源代理清理请求同时携带这些保护路径，保护同路径改内容前仍被其他副本使用的缓存。

源代理抽帧先写入唯一暂存目录，成功后发布独立代际目录，失败清理暂存目录。再次生成较短结果不混入旧尾帧，也不会覆盖共享历史帧。视频批量清除部分成功时立即刷新列表并仅保留未完成项供重试。清除同步失效视频内容散列缓存，并在删除引用前移交仍有其他引用的内容编辑。

新增回归覆盖 `NonLastRemoteReferenceClearsItsDownloadsWithoutTouchingSameIdLocalCache`、`PurgingReplacementContentPreservesOldGenerationUsedByAnotherContent`、`RemoteGenerationPublicationDoesNotIncludeOldTailFramesAndAbortLeavesNoStaging`、`SourceCachePurgePreservesOtherContentGenerationAndClearsUnreferencedGeneration` 和 WPF `PartialVideoPurgeRefreshesCatalogAndRetryOnlyProcessesRemainingSelection`。
