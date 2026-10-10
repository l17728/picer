# 远程视频人物分析 409 排查与修复（2026-09-30）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

## 结论

IndexService 已成功升级并正常接收请求；409 并非部署失败。源照片机上的 `Picer.MediaAgent.exe` 仍是旧版本。服务为视频人物分析指定 `insightface-arcface`，而旧代理可能将本地人脸端点返回的其他 `modelVersion` 原样转交，IndexService 因模型空间不匹配返回 409 `face_model_mismatch`。

## 证据

- 远程服务日志记录视频 `7` 的请求带有 1 个关键帧，并进入 MediaAgent 处理。
- 随后的客户端错误为 HTTP 409；本路由在关键帧缺失时会提前返回，此次已排除该分支。后续 409 分支是 `face_model_mismatch`。
- 正在运行的代理进程使用 `src/Picer.MediaAgent/bin/Debug/net8.0-windows/Picer.MediaAgent.exe`；该文件时间早于当前 `Program.cs`。因此只升级 IndexService 不会更新常驻代理。
- 新代理源码会按请求模型调用端点、校验每帧模型，并以请求模型写入结果，避免旧代理式的模型版本回传错位。
- 重启最新 GUI 后又发现自动启动器的路径解析漏了仓库中的 `src` 目录，日志明确显示“无法启动 MediaAgent：未找到可执行文件”。这导致此前重启 GUI 也没有真正替换/重连代理。

## 修改与验证

- IndexService 在模型冲突时增加仅含 video ID、expected/actual model ID 的安全告警，不记录路径、人物或向量。
- Gateway 对 409 的安全错误白名单增加 `face_model_mismatch` 的 expected/actual 展示；不回显任意响应正文。
- 新增 `AnalyzePeople_ExplainsSafeModelVersionConflict` 回归测试，已通过。
- 修复 `MediaAgentController`：不再假设固定层级，沿 GUI 路径祖先目录查找标准 `src/Picer.MediaAgent` 布局；新增便携 GUI 输出目录解析回归测试，已通过。
- MediaAgent 以隔离输出目录构建通过（0 警告、0 错误）。原 Debug 输出被正在运行的进程锁定，不能覆盖，因此需要应用重启来替换活动代理。
- 按本机程序配置重新启动原始 WPF GUI 后，最新代理已成功启动并连接 `124.156.193.122`；代理日志确认加载了本地人脸服务端点配置。
- 对此前失败的远程视频 ID `7` 重新执行一次真实分析，返回 HTTP 200、`modelVersion=insightface-arcface`、`faceCount=0`。这证明 409 合约冲突已解除；该视频当前关键帧没有检测到人脸，属于成功分析但未发现人脸，不是分析失败。

## 运维说明

IndexService 与驻留 MediaAgent 是两个独立部署/生命周期单元。升级服务端后，源照片机的 GUI 必须能找到并启动本机 MediaAgent；本次已经修复自动发现路径并完成实际重启与真实请求验收。后续若再次出现模型冲突，新版服务日志会明确记录 expected 与 actual 模型标识。
