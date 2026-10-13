# Picer 统一 Service 能力路由 SDD（2026-09-28）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
<!-- /historical-status-2026-10-10 -->

状态：专家评审通过，进入分阶段实现。

## 1. 背景与目标

Picer 的 GUI 必须与模型、媒体处理依赖和云端端点解耦。远程 GUI 与原始照片所在机器分离时，GUI 不得访问本机回环地址、FFmpeg、ASR、TTS、VL 或人脸模型。所有能力统一由 Service 发现、配置、调度和执行。

目标链路：

```text
Picer GUI
  -> IndexService HTTPS（鉴权、索引、任务、流式转发）
  -> MediaAgent（源机文件、FFmpeg、源机模型执行）
  -> ModelServer（模型调度、云端代理、GPU/CPU 资源）
```

模式边界（以当前代码为准）：Remote GUI 使用 `HttpIndexGateway` 读取远程索引镜像；Local GUI 的浏览、搜索和索引写入使用本机 SQLite 与 `LocalGateway`，不会把远程索引当作本地索引。Local 模式可选配置远程 IndexService 用于索引增量发布/同步；同时，AI 客户端通过配对的 IndexService endpoint 发起模型请求。两种模式都不应由 GUI 直接连接 llama.cpp、ASR、VL、TTS 或人脸推理端点。

AI 问答的当前实现链路为：`GUI -> IndexService HTTPS -> 已连接的 MediaAgent WSS -> MediaAgent loopback 模型 HTTP endpoint`。MediaAgent 位于提供原始媒体/本机模型能力的源机；它不是 IndexService，也不是模型推理引擎。`Picer.MediaAgent` 当前从环境变量 `PICER_LOCAL_MODEL_ENDPOINT` 读取问答上游，未设置时默认 `http://127.0.0.1:8090/api/vl/chat`；此 endpoint 不是由 IndexService 配置推送动态覆盖。若该地址由 Local Model Console 暴露，则 Console 后端/其托管模型必须可用，Console 管理 UI 本身不是请求处理进程。8000 端口上的独立 llama-server 不会因进程存在而自动接入 Picer。

重要实现差异：当前客户端的人脸训练/校准平台调用仍有独立路径，`PeopleViewModel` 通过 `PICER_MODEL_SERVER_URL` 和 `PICER_MODEL_SERVER_API_KEY` 直接创建 `FaceTrainingSubmissionService`；它尚未统一走 `IndexService -> MediaAgent`。而问答的 MediaAgent 上游是 `PICER_LOCAL_MODEL_ENDPOINT`（默认 8090），代码没有自动将它改投 ModelServer 8091。故“所有 AI/模型能力均经 IndexService 并由统一平台动态路由”的目标尚未完全实现：训练提交是 GUI 直连例外，问答上游目标也依赖 MediaAgent 进程环境变量。

## 2. 能力边界

统一能力 ID：

| 能力 | 执行职责 |
|---|---|
| `ffmpeg.probe` | 视频时长、分辨率和媒体探测 |
| `ffmpeg.keyframes` | 关键帧生成/复用 |
| `ffmpeg.audio_extract` | 音频提取 |
| `asr.transcribe` | Qwen ASR/Whisper 转写 |
| `vl.describe` | 照片/关键帧描述 |
| `vl.scene_summary` | 场景和视频摘要 |
| `face.detect` | 人脸检测和视频人物分析 |
| `face.embed` | 指定模型空间的人脸向量 |
| `face.calibrate` | 人物原型、阈值和校准制品 |
| `face.train` | ModelServer 训练 Agent 任务 |
| `tts.synthesize` | 中文 TTS 流式合成 |

GUI 只能提交媒体 ID、内容哈希、能力 ID和参数白名单，不能提交本地路径、模型端点、API Key或任意 FFmpeg 参数。

## 3. 统一任务协议

服务端提供：

```text
GET  /api/service/v1/capabilities
GET  /api/service/v1/config (admin)
PUT  /api/service/v1/config (admin)
POST /api/jobs/v1
GET  /api/jobs/v1/{jobId}
GET  /api/jobs/v1/{jobId}/events
POST /api/jobs/v1/{jobId}/cancel
```

任务必须包含：

- `jobId`、`requestId`、幂等键；
- `operation`、`mediaId`、`contentSha256`；
- `modelId`/`modelVersion`（由 Service 绑定，不信任客户端覆盖）；
- `serviceConfigVersion`；
- `sessionId`/`clientId`和授权 scope。

任务状态：

```text
Queued -> Running -> Succeeded
                  -> Failed
                  -> Canceled
                  -> Retrying -> Running
```

结果保存任务指纹、实际执行节点、模型版本、配置版本、输出哈希、进度、耗时和结构化错误码。相同媒体指纹、操作、模型和配置直接复用结果。

## 4. MediaAgent 协议

扩展驻留程序 WebSocket 消息：

```text
agent.register
agent.capabilities
task.open
task.progress
task.result
task.error
task.cancel
task.heartbeat
```

MediaAgent 负责源机上的授权媒体读取、FFmpeg、音频提取、视频关键帧和可配置的源机模型。大文件使用分片/二进制流，不使用无限制 Base64 单消息。MediaAgent 全系统单实例，能力注册包含实际版本、GPU/CPU、资源限制和健康状态。

## 5. 配置与权限

Service Web 配置 UI 是模型和云端能力的唯一配置入口：

- 云端端点、API Key和模型绑定只保存在 Service；客户端不读取密钥；
- 端点使用 HTTPS、白名单和 SSRF 防护；禁止 `file://`、任意内网地址和未授权重定向；
- 配置采用版本号、原子写入、审计和回滚；
- 管理配置需要 `config.admin`；普通客户端只能读取能力状态和任务状态；
- Service 未配置生产鉴权时拒绝对外启动，开发 loopback 模式必须明确显示无鉴权；
- 任务访问校验 `sessionId/clientId`，不能仅凭 job ID 读取或取消任务。

## 6. 本地与远程 GUI 行为

### 远程模式

模型设置、端点、API Key、FFmpeg路径和本地资源选择全部置灰。GUI只读显示 Service 返回的 `ready/configured/degraded/unavailable/remote-only` 状态。

### 本地模式

GUI启动后向本机 Service 查询真实健康状态。只有 Service确认 GPU、模型、FFmpeg、ASR和 TTS 可启动时，相关功能才可用；不能仅依据文件存在或环境变量显示“可用”。

## 7. 数据与隐私

日志只记录 `requestId/jobId/mediaId`、状态、耗时、错误类型和模型版本，不记录人物姓名、问题原文、音频、向量、图片 Base64、密钥或完整云端请求体。原图不被修改；分析结果和派生索引通过 revision/内容哈希增量同步。

## 8. 分阶段实现

当前进度：SVC-001 已完成首个真实任务切片（能力注册、task 协议、`ffmpeg.probe`）；SVC-002 至 SVC-006 仍按下文顺序实施，未接通的能力必须保持 `unavailable`。

1. **SVC-001 协议与能力注册**：统一 capability、job 状态、鉴权 scope和错误码。
2. **SVC-002 MediaAgent 媒体任务**：FFmpeg 探测、关键帧、音频提取、取消、进度和恢复。
3. **SVC-003 ASR/VL/TTS 路由**：转写、描述、摘要和 TTS 全部经 Service，支持流式。
4. **SVC-004 人脸任务**：视频人物、向量补齐、校准和训练提交迁移到 Service。
5. **SVC-005 Service Web 配置 UI**：资源探测、云端配置、模型绑定、审计和回滚。
6. **SVC-006 GUI 收口**：删除远程直连、置灰远程配置、本地也只调用 Service。

## 9. 验收与回归

- 路由契约（已实现部分）：GUI 的 AI 问答、媒体、ASR、VL/TTS 和人脸推理不直接连接本机模型进程；人脸训练提交仍有 `PeopleViewModel -> ModelServer` 直连例外，尚未完全满足统一路由目标；
- 能力契约：每项任务都有进度、取消、重试、幂等和结构化失败；
- Local/Remote 同一视频：时长、分辨率、关键帧哈希、ASR、人物框、摘要和状态一致；
- 故障：Agent 离线、Service 503、云端 401/429/5xx、FFmpeg失败、模型缺失、哈希变化和断线恢复；
- 安全：密钥不下发、任意端点拒绝、无鉴权生产服务拒绝启动、任务归属校验；
- UX：提交立即显示 Queued，流式首块及时到达，取消不阻塞 UI，Service 不可用时明确提示。

## 10. 专家评审结论

架构、媒体管线、安全与客户端 UX 的主要迁移已完成；但代码复核发现不能将 SVC-001 至 SVC-006 宣称为完全收口：普通模型/媒体代理经 IndexService → MediaAgent，AI 问答模型 endpoint 仍由 MediaAgent 进程环境变量决定；GUI 人脸训练提交仍通过 `PICER_MODEL_SERVER_URL` 直连 ModelServer，没有经过 IndexService。后续必须收敛训练路由及服务端 endpoint 配置，再完成部署环境验收。详见 `MEDIA_AGENT_REAL_ENDPOINT_E2E_2026-09-28.md` 与本节“模式边界”。
