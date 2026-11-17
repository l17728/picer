# 目录描述取消与失败定位：第一批实施记录

后续状态：本文件保留第一批证据；批量上传、差分回读、独立同步队列和有界输入预取已继续实现，当前验证见[剩余开发收尾记录](REMAINING_DEVELOPMENT_2026-10-30.md)。本文件中的“尚未实现”仅对应当时阶段。

日期：2026-10-30。对应[定稿设计](DESCRIPTION_PIPELINE_SDD_2026-10-30.md)、[UX](DESCRIPTION_PIPELINE_UX_2026-10-30.md)、[评审](DESCRIPTION_PIPELINE_REVIEW_2026-10-30.md)。代码未发布、未替换现用 GUI/Agent/服务，没有生产迁移。

## 第一批变更

- 统一窗口取消入口，立即提示并禁用重复点击；runId/状态隔离迟到进度，同步捕获最终计数。
- 周期同步状态读取异常与资源释放异常隔离；独立 finally 恢复运行句柄和按钮，避免再次开始永久被挡。
- 用户取消不计失败；输入/视频拼图临时文件删除失败不覆盖原始取消或成功结果。
- 已完成模型结果保持不可取消持久化。MarkCanceled 或预算释放失败单独记录，不把主结果变成失败。成功保存通过 Billing.Record 消费预算预留；失败释放保留现有一小时过期预留，下次 TryReserve 清理过期项。取消标记失败留下未完成状态，下次描述可重做；不是假装状态已写成功。
- 记录取消请求、worker 收敛及五秒等待；数据库等待区分 queue/execute，标明保存结果、取消状态、预算释放。服务转发 ai.cancel 最长等待五秒，Agent 记录收到和上游 HTTP 取消；不能据此断言第三方模型内部计算已停止。
- PhotoInputAnalyzer 在 prepare_cache/prepared_input/original_open/copy/verify/model/cleanup 记录首次异常及耗时；模型图像准备细分 open/buffer/decode/resize/encode。持久化错误前记录异常链、HRESULT、HTTP/Socket 状态，禁止路径、正文、人物、凭据。
- 高频诊断采用 256 条有界非阻塞队列，满时丢诊断并汇总计数，避免机械硬盘写日志反过来挡住取消。正常快速数据库操作不逐条输出。

## 验证记录

静态双 reviewer 已确认阶段 A 两项阻塞关闭：日志阻塞和清理异常覆盖结果；最后视频清理日志也改为非阻塞。

构建/测试证据目录：`E:\picer-description-pipeline-20261030`。首次严格构建因原 C 盘 NuGet 缓存中的依赖 DLL 缺失失败（MSB3106），不属于通过记录。依赖已恢复到 `E:\picer-nuget-packages`；新增测试的一处命名空间引用已修正。最终完整解决方案 Debug `--warnaserror` 构建通过，0 警告、0 错误，证据 `build-final.log`。关闭离线运行资产复制，未执行发布门禁。

新增测试覆盖生命周期收尾/进度隔离、临时文件占用、数据库等待取消与持久化结果保护，以及照片/视频成功/取消时收尾 SQLite 故障和预算到期恢复。

- 核心专项 51/51 通过，0 失败/跳过：AnalysisDatabaseWork、DescriptionCleanupFailure、CloudVision、VideoSummaryConcurrency/Policy、AgentConnectionRegistry、AnalysisStageException、DescriptionPerformance；证据 `core-tests.log` 及 `core-results/*.trx`。
- 目录描述 E2E 工程专项 24/24 通过，0 失败/跳过：DirectoryMediaDescriptionTests；证据 `e2e-tests.log` 及 `e2e-results/*.trx`。包含生命周期/调度/输入测试与既有窗口测试，不等于现场用户整段目录取消已复现验收。
- 均排除 RuntimeComponent；未执行全量核心/E2E回归、真实模型压力测试或完整发布门禁。没有运行候选 GUI 打开生产库。

后续只读复核 Windows 分页：自动管理开启；G 盘 pagefile 分配约 46.2 GiB、使用约 18.0 GiB；同时 C 盘约 2.9 GiB、当前接近零使用。注册表 ExistingPageFiles 当时仍仅列 G，与运行期 WMI 不完全一致，判断当前活动以运行期采样为准；不能声称系统已主要使用 C 盘分页。没有修改系统分页配置。

## 边界

这批修复不消除共享数据库门的长等待，只能保证正确收尾并定位等待位置；长镜像事务拆分、描述专用批量上传、记录级差分、水位屏障及有界预取仍按 B/C/D 后续实施。长期内存泄漏验证尚未执行。现用程序不会自动得到这些新日志，需完成候选验证后再按用户安排切换。
