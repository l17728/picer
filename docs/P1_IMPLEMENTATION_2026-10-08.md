# P1 实施与验收（2026-10-08）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

本次承接五项 P1：训练统一路由、视觉概念过滤、失败任务实际重试、人工元数据同步保护、自包含发布与离线验收。保留此前 P0 和工作区已有修改，不提交或回退其他改动。

## 实现

1. 人物训练、校准窗口、模型列表、评估集、历史、制品、签名、公钥、应用标记、取消和重启监控经已配对 IndexService 转发至 MediaAgent，再访问本机 ModelServer。GUI 不再读取 ModelServer 密钥或跳过 TLS 校验。服务和代理共享训练路由白名单，保留上游 HTTP 状态；训练能力只标记 configured，模型实际可用性由 algorithms 返回。训练失败分析沿用服务端 AI 通道。
2. `MediaSearchTool` 将 VisualConceptIds 写入结构化条件，文字查询和 GPS 放宽保留概念约束；概念按 AND 匹配，人工拒绝结果不再召回。修复 GPS 放宽未清除边界、重复标签和视频候选计数。尚无对应结构化索引的视频组合条件明确提示并排除，不静默忽略。
3. 视频探测、音频提取、关键帧、转写和人物分析保留可重放的媒体 ID 请求。重试执行完整服务路由，重新校验并入库，返回实际结果；重复重试返回 409。请求和结果保存于独立 task-requests 目录，诊断日志只记任务状态。结果可通过 `/api/service/v1/tasks/{id}/result` 获取。重启仅把最终状态仍在运行的任务标为 abandoned，不再把历史上运行过的成功任务误标失败。旧任务没有请求数据时明确拒绝重试；流式和训练提交不自动重放。
4. schema v39 增加 `metadata_edits`，事务记录人物改名、别名、描述，以及图片标签/相册关系的添加和删除标记。小批独立同步、确认后保留记录、重启重放；同字段按 UTC 时间、操作编号确定同一胜者。未知实体不确认，待媒体/人物同步后重试。旧快照不能覆盖已记录的改名，关系快照应用后重放删除标记。此为逐字段冲突收敛，不是全量服务端版本序列协议；实体创建、删除/合并和其他未接入字段仍沿用现有协议。
5. Windows 自包含发布增加驻留程序 exe/dll/runtimeconfig/deps 和运行时完整性检查，以及 Magick 原生库与许可证的固定哈希检查。Linux 包补入 Magick 许可证，使用跨平台 ZIP 路径；安装脚本在停服后整体备份数据库及 WAL/SHM，再升级程序。

## 已完成验证

- Debug 全解决方案严格构建：0 警告、0 错误。
- 非 RuntimeComponent 单元/服务测试：400/400，`artifacts/p1-tests/p1-unit.trx`。
- GUI E2E：60/60，`artifacts/p1-tests/p1-e2e.trx`。
- 最后训练日志路径修正后的针对性回归：46/46，`p1-final-delta.trx`。
- RuntimeComponent：Core 2/2；E2E 7/7。其中外部 Qwen 实时语音测试因未设置 `PICER_RUN_REAL_MODEL_E2E=1` 不执行模型请求，不能计为真实在线语音验收。其余覆盖 CLIP、Florence、人脸模型、FFmpeg、Whisper、AEC 和资源哈希。
- 发布目录：`artifacts/p1-release-20261008/Picer-win-x64` 与 `Picer.IndexService-win-x64`。发布资源及许可证检查通过。
- 自包含 GUI 实际启动：独立 bootstrap/data、本地模式，加载包内 coreclr.dll，窗口正常响应并正常关闭。记录位于 `artifacts/p1-tests/package-smoke-c28f40b70e4746d7bff4f03a6cfceecc/result.json`。
- 本地迁移前备份：`artifacts/p1-schema-backup-20261008`，包含 picer.db、remote-mirror.db 及现存 WAL/SHM；备份时 GUI 与驻留程序已关闭。

## 交付收尾

- Linux 部署 `20261008T074139942Z` 已通过线上健康/版本检查，schema 39。程序备份 `/opt/picer-index.backup-20261008T074139942Z`；停服后的整库备份位于其 `database` 子目录。首次解压因 Windows ZIP 路径警告中止，未替换程序；改用正斜线 ZIP 条目后成功部署。
- 真实训练路由返回 6 个算法、6 个可用模型、1 条训练历史；元数据编辑接口 HTTP 200。
- 真实视频 7 分析 HTTP 200，insightface-arcface，1 张人脸。
- GUI PID 17368，窗口“Picer - 图片管理（远程）”正常响应；驻留程序 PID 6248 存活。本地镜像 schema 39。全库 ignored 标记 10437 条；单独核对用户历史人工忽略的 14 张人脸，14/14 仍为 ignored，没有恢复为待分类。
- 客户端 ZIP `artifacts/p1-release-20261008/Picer-win-x64.zip`，服务端 ZIP `artifacts/p1-release-20261008/Picer.IndexService-win-x64.zip`；两包全部 ZIP 条目 CRC 通过，旁附 SHA-256。计数、哈希及最终状态见 `artifacts/p1-tests/final-manifest.json`。
- 可复用的实际包 GUI 验收工具：`tools/verify-p1-package.ps1`。
- 已落实本轮五项 P1 的上述实现与验收范围；不把未执行的外部 Qwen 语音测试或训练准确率提升计为完成。
