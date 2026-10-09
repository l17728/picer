# Picer Model Server

模型服务端，与 `src/Picer.App` 客户端分离，负责模型目录、VL/ASR/TTS 路由、GPU 调度和人脸训练任务生命周期。远程索引与同步 API 属于独立的 `Picer.IndexService`。

## 发布与恢复（2026-10-09）

IndexService 当前数据库 schema 为 45，须与客户端配套升级。Linux 升级必须先停所有数据库写入者，备份 DB/WAL/SHM；失败时数据库和程序一起恢复，不能只降级二进制。具体命令、额外写入服务配置、关闭快照和失败停机行为见 [自动升级说明](../docs/INDEX_SERVICE_AUTOMATED_UPGRADE_2026-09-30.md)。

本机 ModelServer 使用 `tools/deploy-local-modelserver.ps1`，默认模型根 `D:\VL-models`。脚本先发布完整 Release 输出，停止经过路径核验的 ModelServer 及子 worker，备份整个服务目录、启动脚本和训练归档，再安装所有依赖及仓库 worker，启动并检查 `/health`。失败时保存失败目录，恢复完整应用和训练归档后启动旧服务。模型文件根不会被清理。

可用 `-SourceDirectory` 指定已经发布的 staging 目录；不能指向当前安装。若启动脚本配置了外部 `PICER_TRAINING_ARCHIVE_ROOT`，必须通过 `-TrainingArchiveRoot` 明确传入实际归档目录，避免遗漏有状态数据。调用前须暂停其它会自动拉起服务的调度器，确保该训练目录没有其它进程写入。脚本不输出启动脚本内容或密钥；备份可能包含配置和私人训练数据，应保留原有访问控制并妥善保存。默认服务监听 `127.0.0.1:8091`，现有启动脚本及其模型运行环境须已配置。

## MediaAgent 能力配置

Remote GUI 通过 `HttpIndexGateway` 使用 IndexService 的远程索引。Local GUI 的浏览、搜索和索引写入仍使用本机 SQLite/`LocalGateway`；它可选配远程 IndexService 做索引同步，而 AI 请求通过配对的 IndexService 中继。两种模式都不需要本机再启动一套 `Picer.IndexService`。媒体源机运行 `Picer.MediaAgent`，通过 WebSocket 连接 IndexService；AI 请求由 IndexService 转发给 MediaAgent，再由 MediaAgent 调用配置的模型端点。它依据下列变量注册真实能力；未配置的端点不会显示为 ready：

- `PICER_FFMPEG_PATH`：ffmpeg.exe 路径（同目录可选 ffprobe.exe）。
- `PICER_LOCAL_ASR_ENDPOINT`：本机 Qwen3-ASR/Whisper HTTP 端点。
- `PICER_LOCAL_VL_ENDPOINT`：本机 VL HTTP 端点。
- `PICER_LOCAL_TTS_ENDPOINT`：本机中文 TTS HTTP 端点。
- `PICER_LOCAL_FACE_ENDPOINT`：本机人脸检测和向量推理 HTTP 端点；通过 `X-Picer-Face-Operation` 区分操作。
- `PICER_LOCAL_TRAINING_ENDPOINT`：可选本机 ModelServer 根地址；仅允许 loopback。未配置时，可从 `/api/face-inference` 推理地址推导同源训练服务。

视频人物检测可使用现有 ModelServer 的 `http://127.0.0.1:8091/api/face-inference`。通过 IndexService 的 `PUT /api/service/v1/config` 更新 `PICER_LOCAL_FACE_ENDPOINT` 可持久化并广播端点；训练采用独立的 `training.proxy` 白名单通道，不向推理接口发送训练协议。旧开发适配器的 8092 端口只有单独启动时才可用，不能作为常驻服务使用。

`PICER_LOCAL_MODEL_API_KEY` 为代理显式凭据，优先使用。未设置时，HTTP(S) 回环端点复用本机 ModelServer 的凭据解析（`PICER_MODEL_SERVER_API_KEY`、用户 `.picer/model-server-key` 或既有本机 secrets 文件）；远程端点不读取或附加自动发现的本机凭据。密钥不通过 IndexService 配置广播。

IndexService 的 `/service` 页面和 `/api/service/v1/capabilities` 会显示 Agent 实际注册状态。API Key 只应保存在 Service/Agent 侧，不能写入 GUI 配置或日志。

AI 问答上游有单独实现：`PICER_LOCAL_MODEL_ENDPOINT` 是 MediaAgent 进程环境变量，默认 `http://127.0.0.1:8090/api/vl/chat`；它当前不是 IndexService 广播配置项。若此 URL 指向 Local Model Console 暴露的模型 API，则 Console 的后端模型服务必须运行；仅打开 Console 管理网页不够。运行在 `8000` 的 `llama-server` 也不会自动接入，除非将 MediaAgent endpoint 显式指向它兼容的 API。

2026-10-08：GUI 人脸训练、校准、历史与制品统一通过 `/api/service/v1/training/api/face-training/*`，使用配对凭据与固定公钥。代理转发至本机 ModelServer 并在本机注入模型密钥；GUI 不直接访问 ModelServer。训练能力 configured 表示已配置通道，实际模型就绪状态读取 algorithms。

Picer 客户端通过 HTTPS 读取和更新远程 index，不依赖服务端本地文件路径或 SQLite 结构；模型推理同样只通过服务 API 调用。客户端本地缓存不可作为远程事实源，断网时只能显示最近一次缓存并明确标注离线状态。

当前提交的是可运行的 API 骨架：训练执行器和制品签名需要接入实际 GPU worker 后才会产生真实制品，服务不会伪造训练结果。

部署：开发环境可直接 `dotnet run --project server/Picer.ModelServer`；生产环境使用 HTTPS 反向代理或配置 Kestrel 证书，并设置 `PICER_MODEL_SERVER_API_KEY`。Dockerfile 已提供基础镜像，GPU worker 以独立服务接入，不把 CUDA/模型文件打进 API 镜像。
## AI 服务路由

`PICER_LANGUAGE_MODEL_ENDPOINT` 配置语言模型服务（训练失败分析使用智谱）；`PICER_VL_MODEL_ENDPOINT` 配置统一平台管理的本地 VL 服务（训练样本视觉筛选为可选增强）。VL 未配置时，向量训练仍可正常提交，不会阻塞任务。可通过 `/api/ai/capabilities` 查看能力状态。
