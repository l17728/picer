# 人工不分类状态同步保护（2026-10-08）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
> 验收边界：本文训练或人脸准确率数字仅对应当时模型、数据分区和设备，不能代替当前质量门禁；人脸质量等四项旧 P2 验收仍暂停，模型向量保持版本隔离。
<!-- /historical-status-2026-10-10 -->

## 问题与实现

14 张人脸的人工“不分类”已写入操作日志，但整包上传失败，启动时远程旧快照通过 `DELETE FROM face_review_states` 覆盖本地状态，导致重复出现。

本次复用既有 `face_corrections` 事务日志作为持久化人工决定，不新增数据库表或变更 schema v38。按 face ID 选择最新时间的决定；相同时间按状态字符串排序确定一致结果。`ignored` 表示不分类，`active` 明确表示恢复分类，不能用集合中缺少某条记录表达恢复操作。用户确认人物时也记录解除不分类的决定。

- 增加受现有 Bearer 认证保护的 `POST /api/sync/v1/face-review-decisions`，每批最多 500 条，只传 ID、状态、时间，不传图片或向量。
- 服务端按时间幂等合并，返回实际接收的人脸最新决定；未知人脸不确认，客户端保留重试。重复请求不重复写入纠正日志。
- 客户端只缓存已确认的发送版本指纹；请求期间的新编辑不会被误标为已发送。操作日志在确认后仍保留，重启后可重新提交。
- 人工决定使用独立连接同步；即时操作另起小请求，不等待已经运行中的媒体/向量全量上传。
- 客户端启动拉取快照前先尝试同步人工决定；即使失败，应用旧快照时仍按本地日志恢复有效决定。
- 服务端及客户端替换工作流快照后重放人工决定；批量人脸/人物关系上传也保护已忽略的人脸，避免旧发布端重新引入分配和候选关系。
- 元数据快照附带最新人工决定，其他客户端能够收到明确的恢复分类记录。

该合并规则依赖 UTC 操作时间排序；没有将此实现表述为具有服务器分配序号的完整多客户端冲突协议。人物命名等其他字段的同步不在本次修复范围。

## 实际数据与备份

- 本机镜像中的历史日志可补回恰好 14 张人脸，不移除现有其他 ignored 标记。
- 本机权威库对这 14 张没有较新的人工决定，旧发布快照不能覆盖已恢复状态。
- GUI 关闭后已备份本机 `picer.db`、`remote-mirror.db` 及存在的 WAL/SHM 到 `artifacts/face-review-backup-20261008-100415`。
- 服务端安装脚本保留旧可执行文件并支持健康检查失败回滚；部署前另做一致性数据库备份。

## 验证

- 非 RuntimeComponent 单元全套：370/370 通过（含 7 项新增专项与接口验证）。报告：`artifacts/face-review-tests/face-review-sync.trx`。
- 非 RuntimeComponent E2E 全套：60/60 通过。报告：`artifacts/face-review-tests/face-review-e2e.trx`。
- 服务端部署 `20261008T020500840Z` 完成且健康/版本校验通过；数据库备份 `/var/lib/picer-index/backups/face-review-20261008T020500840Z.db`，旧程序备份 `/opt/picer-index.backup-20261008T020500840Z`。
- 首次由新版 GUI 启动恢复：本地 ignored=14、服务端 ignored=14、服务端对应操作日志=14，实际待分类 SQL 返回 0。证据：`artifacts/face-review-tests/first-start.json`。
- 再次关闭并重启 GUI，10:15:35 完成启动镜像同步；随后复核仍为本地 ignored=14、服务端 ignored=14、服务端对应操作日志=14、待分类=0。证据：`artifacts/face-review-tests/second-start.json`。GUI 保持打开，驻留代理由 GUI 自动启动。

构建环境说明：初次 Debug 严格构建通过。Linux 打包刷新依赖审计后，原有 `SixLabors.ImageSharp 2.1.11` 出现 NU1902/NU1903 告警，后续 `--warnaserror` 因此失败，编译产物已生成。未禁用审计、未压制告警、未在本次同步修复中升级 ImageSharp。告警包含 [TIFF 编码问题](https://github.com/advisories/GHSA-jjfr-hcj7-qf5w) 和 [ICC 解析问题](https://github.com/advisories/GHSA-gwg2-r3hj-4w44)，依赖升级需单独完成验证。
