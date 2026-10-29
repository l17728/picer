# 交互索引读取第一步修复

## 问题与范围

承接 [分段耗时诊断](INDEX_READ_LATENCY_DIAGNOSIS_2026-10-28.md)。视频详情曾在后台 Faces / FaceWorkflow 写入占用 `UiWork.DatabaseGate` 时等待约 100 秒，再在关键帧加载阶段重复排队。此次仅处理交互读取隔离、详情快照复用和缓存优先显示；同步差量化、远程未缓存帧的传输优化留在后续步骤。

## 实现

- 文件数据库通过独立 `ReadOnly` 连接读取，沿用迁移生命周期租约；不执行迁移或修改 journal mode。事务内先固定 WAL 快照，回调内完成结果物化，结束即释放连接和快照。
- `UiWork.ReadSnapshotAsync` 在后台线程执行，不经过写入队列。写操作保持原有串行调度；独立 `:memory:` 测试库使用串行回退。日志记录 `DatabaseRead … snapshotMs`。
- 视频目录查询以及选中视频、转写、摘要、关键帧、人物标注与人物列表使用只读快照。详情将已读取的视频记录交给关键帧缓存加载，取消再次全表查视频及再次等待写锁。
- 已缓存帧及对应人物缩略图先展示，缺失帧继续下载，完成后统一刷新；保留选择切换取消和旧请求结果保护。
- 人物页的模型列表、覆盖率、训练历史、本地分类概览、命名人物照片/视频人脸、待分类与不分类列表、候选组排序、连拍组查询改用独立读取。特征排序在读取事务结束后执行。

## 验证与交付

构建、测试产物位于 `E:\picer-description-sync-20261028`，使用 `--artifacts-path` 和 `PICER_TEST_TEMP`，构建关闭 runtime 资产复制。测试结果见该目录 `test-results/step1-*.trx`。

- 完整解决方案 Debug 构建，`--no-restore --warnaserror`：0 警告、0 错误。
- Core 视频/人物/迁移相关回归：209/209 通过、0 跳过，用时 13 分 26 秒。筛选条件为 `(FullyQualifiedName~DatabaseReadSnapshotTests|FullyQualifiedName~DatabaseMigration|FullyQualifiedName~Video|FullyQualifiedName~People|FullyQualifiedName~FaceFeatureOrder)&Category!=RuntimeComponent`。
- 上述 209 项包含 4 项快照专项：未提交写入隔离、跨提交快照一致、只读约束/异常释放、取消/内存回退约束，不重复相加。
- WPF 回归：19/19 通过，包括新增并发用例、视频详情、人物模型空间、人物缩略图生命周期。后台同时持有 SQLite 写事务和 `UiWork.DatabaseGate` 时，测试中的视频列表及详情用时 307 ms，读取完成时写锁尚未释放；这是隔离小数据用例，不代表现场大图库实测。
- 缓存用例通过受控 HTTPS handler 挂起缺失帧响应，断言已有帧先显示，并在最终响应后不重复。

本轮未运行全量 CI 或 `RuntimeComponent`，不声明离线 runtime 验收完成。

当前 GUI、后台分析、索引服务均未重启或替换；未发布，也未操作原始照片/视频。运行中的旧版本尚未获得本次修复，现场速度须在用户结束当前任务并切换候选程序后手工验收。本候选同时包含先前未发布的 v92 描述同步修复，不能绕过原有闭库迁移与备份保护。

手工验收：后台同步运行时进入视频列表、选中有缓存关键帧的视频，确认转写/摘要/人物与缓存帧及时出现；切换人物分类及命名人物；快速切换两个视频，确认没有旧视频覆盖；选择含缺失帧的视频，确认缓存先显示，缺失帧稍后补齐或提示不可用。未缓存帧的网络耗时不属于本次修复的完成承诺。
