# Picer Model Server

模型服务端，与 `src/Picer.App` 客户端分离，负责模型目录、VL/ASR/TTS 路由、GPU 调度和人脸训练任务生命周期。远程索引与同步 API 属于独立的 `Picer.IndexService`。

## 发布与恢复（2026-10-10）

IndexService 新增独立后台一致性检查：启动后 45 秒开始，每轮间隔 10 秒，照片与视频交替，每批 32 条并轮转内容审计。仅在单个支持 `directory.verify.v1` 的来源代理在线时执行，遇到前台工作让路；不扫描新文件，不自动清除索引。设置 `PICER_BACKGROUND_INDEX_CHECK=0` 并重启服务可关闭。检查点位于服务数据目录的 `consistency-checkpoint.json`，只有数字游标。更多协议、兼容和发布证据见[七项性能与后台维护改造](../docs/PERFORMANCE_FOLLOWUP_IMPLEMENTATION_2026-10-10.md)。

生产 IndexService 已升级为 v1.6.2/schema91；正式发行状态见[发布记录](../docs/RELEASE_1_6_2_2026-10-27.md)，实际部署及校验见[部署记录](../docs/DEPLOYMENT_V1_6_2_2026-10-27.md)。GUI/驻留安装器及 IndexService、ModelServer 的 Windows/Linux x64 组件包由发行流水线提供。账号空间按来源隔离同步，媒体仍由有权限的在线来源代理提供；升级采用全空间关闭写入备份、离线迁移、运行容量预检及可恢复回滚。仓库为私有，下载需要访问权限。组件职责与首次配置见[安装指南](../docs/RELEASE_GUIDE.md)。

IndexService 当前数据库 schema 为 45，须与客户端配套升级。Linux 升级必须先停所有数据库写入者，备份 DB/WAL/SHM；失败时数据库和程序一起恢复，不能只降级二进制。具体命令、额外写入服务配置、关闭快照和失败停机行为见 [自动升级说明](../docs/INDEX_SERVICE_AUTOMATED_UPGRADE_2026-09-30.md)。

本机 ModelServer 使用 `tools/deploy-local-modelserver.ps1`，默认模型根 `D:\VL-models`。脚本先发布完整 Release 输出，停止经过路径核验的 ModelServer 及子 worker，备份整个服务目录、启动脚本和训练归档，再安装所有依赖及仓库 worker，启动并检查 `/health`。失败时保存失败目录，恢复完整应用和训练归档后启动旧服务。模型文件根不会被清理。

可用 `-SourceDirectory` 指定已经发布的 staging 目录；不能指向当前安装。若启动脚本配置了外部 `PICER_TRAINING_ARCHIVE_ROOT`，必须通过 `-TrainingArchiveRoot` 明确传入实际归档目录，避免遗漏有状态数据。调用前须暂停其它会自动拉起服务的调度器，确保该训练目录没有其它进程写入。脚本不输出启动脚本内容或密钥；备份可能包含配置和私人训练数据，应保留原有访问控制并妥善保存。默认服务监听 `127.0.0.1:8091`，现有启动脚本及其模型运行环境须已配置。

## MediaAgent 能力配置

Remote GUI 通过 `HttpIndexGateway` 使用 IndexService 的远程索引。Local GUI 的浏览、搜索和索引写入仍使用本机 SQLite/`LocalGateway`；它可选配远程 IndexService 做索引同步，而 AI 请求通过配对的 IndexService 中继。两种模式都不需要本机再启动一套 `Picer.IndexService`。媒体源机运行 `Picer.MediaAgent`，通过 WebSocket 连接 IndexService；AI 请求由 IndexService 转发给 MediaAgent，再由 MediaAgent 调用配置的模型端点。它依据下列变量注册真实能力；未配置的端点不会显示为 ready：

- `PICER_FFMPEG_PATH`：可选管理员 FFmpeg 路径覆盖。v1.3.5 的 GUI 同包代理及独立代理安装器均附带 FFmpeg、FFprobe 和许可证，并自动发现；普通用户无需设置该变量。手动覆盖时应同时核对相应探测工具是否可用。
- `PICER_LOCAL_ASR_ENDPOINT`：本机 Qwen3-ASR/Whisper HTTP 端点。
- `PICER_LOCAL_VL_ENDPOINT`：本机 VL HTTP 端点。
- `PICER_LOCAL_TTS_ENDPOINT`：本机中文 TTS HTTP 端点。
- `PICER_LOCAL_FACE_ENDPOINT`：本机人脸检测和向量推理 HTTP 端点；通过 `X-Picer-Face-Operation` 区分操作。
- `PICER_LOCAL_TRAINING_ENDPOINT`：可选本机 ModelServer 根地址；仅允许 loopback。未配置时，可从 `/api/face-inference` 推理地址推导同源训练服务。
- `PICER_LOCAL_CLIP_ENDPOINT`：本机 ModelServer 的 CLIP 向量端点，通常为 `http://127.0.0.1:8091/api/clip/embedding`。未配置时，可从训练端点推导。GUI 不加载或持有 CLIP 模型。

视频人物检测可使用现有 ModelServer 的 `http://127.0.0.1:8091/api/face-inference`。通过 IndexService 的 `PUT /api/service/v1/config` 更新 `PICER_LOCAL_FACE_ENDPOINT` 可持久化并广播端点；训练采用独立的 `training.proxy` 白名单通道，不向推理接口发送训练协议。旧开发适配器的 8092 端口只有单独启动时才可用，不能作为常驻服务使用。

`PICER_LOCAL_MODEL_API_KEY` 为代理显式凭据，优先使用。未设置时，HTTP(S) 回环端点复用本机 ModelServer 的凭据解析（`PICER_MODEL_SERVER_API_KEY`、用户 `.picer/model-server-key` 或既有本机 secrets 文件）；远程端点不读取或附加自动发现的本机凭据。密钥不通过 IndexService 配置广播。

IndexService 的 `/service` 页面和 `/api/service/v1/capabilities` 会显示 Agent 实际注册状态。模型 API Key 保存在 Service/Agent 侧，不写入 GUI 配置或日志；GUI 连接 IndexService 的配对令牌是另一种凭据，由当前 Windows 用户 DPAPI 保护。

AI 问答上游有单独实现：`PICER_LOCAL_MODEL_ENDPOINT` 是 MediaAgent 进程环境变量，默认 `http://127.0.0.1:8090/api/vl/chat`；它当前不是 IndexService 广播配置项。若此 URL 指向 Local Model Console 暴露的模型 API，则 Console 的后端模型服务必须运行；仅打开 Console 管理网页不够。运行在 `8000` 的 `llama-server` 也不会自动接入，除非将 MediaAgent endpoint 显式指向它兼容的 API。

2026-10-08：GUI 人脸训练、校准、历史与制品统一通过 `/api/service/v1/training/api/face-training/*`，使用配对凭据与固定公钥。代理转发至本机 ModelServer 并在本机注入模型密钥；GUI 不直接访问 ModelServer。训练能力 configured 表示已配置通道，实际模型就绪状态读取 algorithms。

Picer 客户端通过 HTTPS 读取和更新远程 index，不依赖服务端本地文件路径或 SQLite 结构；模型推理同样只通过服务 API 调用。客户端本地缓存不可作为远程事实源，断网时只能显示最近一次缓存并明确标注离线状态。

## Chinese-CLIP 本地部署

自动分类本身只整理已有描述和可核验元数据，不触发新模型推理。可选的“BM25 + CLIP”多路召回走固定链路：GUI → IndexService → 来源 MediaAgent → 来源机 ModelServer。图片在来源机校验授权目录后缩放为 224×224、清除元数据，再发送给回环 ModelServer；原始路径和原图不会发送到 GUI 或 IndexService。

在 ModelServer 进程设置 `PICER_CLIP_MODEL_ROOT`，目录中放置 `models/chinese-clip/model_quantized.onnx` 与 `models/chinese-clip/vocab.txt`；也可分别使用 `PICER_CLIP_MODEL_PATH`、`PICER_CLIP_VOCAB_PATH`。`PICER_CLIP_CONCURRENCY` 可设为 1–8，默认按 CPU 核数选择 1–2。模型缺失时 CLIP 请求明确失败，不影响 ModelServer 健康检查、BM25 搜索或已有分类。MediaAgent 还需设置 `PICER_LOCAL_CLIP_ENDPOINT`，并使用 `PICER_LOCAL_MODEL_API_KEY` 或本机 ModelServer 密钥。模型和 ONNX 推理依赖部署在 ModelServer，不属于 GUI 安装包。

仓库包含常驻 Python 人脸 worker，支持 CPU 或可选 CUDA 的向量校准、图像向量提取及检测。ModelServer 通过子进程协议调用它；配置 Python、脚本和所选模型后才能提供对应能力，未配置时不伪造训练结果。

部署：开发环境可直接 `dotnet run --project server/Picer.ModelServer`；生产环境使用 HTTPS 反向代理或配置 Kestrel 证书，并设置 `PICER_MODEL_SERVER_API_KEY`。Dockerfile 已提供基础 API 镜像，但没有 Python、CUDA 或模型；使用人脸 worker 时须另行准备包含这些依赖的运行环境。

## 一条命令准备人脸 worker 环境

使用 [setup-modelserver.py](setup-modelserver.py) 创建独立 venv；支持 **Windows/Linux x64、Python 3.10–3.12**。如果 `py`/`python3.12` 命令不存在，先安装对应的 64 位 Python；Linux 还需发行版的 `python3.12-venv`（实际包名随发行版变化）。脚本用调用它的解释器创建环境，输出供 ModelServer 使用的绝对 Python 路径。默认环境位于 Windows `%LOCALAPPDATA%/PicerModelWorker/<device>` 或 Linux `~/.local/share/picer-model-worker/<device>`，也可用 `--venv` 指定服务用户可访问的独立目录。发布包中将此脚本、两份 `modelserver-requirements-*.txt` 与 `worker/` 放在同一目录；源码布局也会自动识别。

以下命令从仓库根目录执行；发布包内改用 `setup-modelserver.py`。先准备自行合法取得的 SFace 和 YuNet 模型；脚本不会下载模型，也不会修改驱动、全局 Python 或启动/重启服务。

Windows PowerShell：

```powershell
py -3.12 server/setup-modelserver.py --venv D:/PicerModelWorker/cpu --model-path D:/PicerModels/face_recognition_sface_2021dec.onnx --detector-path D:/PicerModels/face_detection_yunet_2023mar.onnx
# 在实际启动 ModelServer 的同一个进程环境中加载：
. D:/PicerModelWorker/cpu/worker-env.ps1
```

Linux（以运行服务的专用用户执行，目录须可写）：

```bash
python3.12 server/setup-modelserver.py --venv "$HOME/.local/share/picer-model-worker/cpu" --model-path /srv/picer-models/face_recognition_sface_2021dec.onnx --detector-path /srv/picer-models/face_detection_yunet_2023mar.onnx
. "$HOME/.local/share/picer-model-worker/cpu/worker-env.sh"
```

生成的片段仅包含 `PICER_GPU_WORKER_PYTHON`、`PICER_GPU_WORKER_SCRIPT`、`PICER_GPU_WORKER_USE_CUDA` 和显式选择的模型路径，不包含 API Key。Windows 启动脚本应先 dot-source 此片段再启动程序；Linux 启动 shell 应先 source 再 `exec` ModelServer。**`worker-env.sh` 是 shell 脚本，不能直接作为 systemd 的 `EnvironmentFile`**。服务凭据继续由原有受保护配置提供；在交互终端加载环境不会自动改变已经运行的服务。

首次运行联网安装依赖，然后检查依赖版本、数值运算、模型加载及 GPU 实际可用性。路径不存在会先报中文修复指引，不下载数 GB 的依赖后才告知缺少模型。再次用同一命令可修复未完成的安装；不要对正在使用的 venv 执行安装，升级时创建新目录并在停服务后切换。非本脚本创建的环境、CPU/CUDA 混用或 Python 主次版本不同的环境会被拒绝覆盖。

- 在原安装命令末尾加 `--check`：只读校验，不创建环境、不联网安装或写配置；仍需同样的模型参数，或事先加载生成的环境片段。
- 没有图像推理模型、只做**预计算向量校准**：以 `--embeddings-only` 替代两个模型路径参数。此检查不证明人脸检测/图片向量提取可用。
- 选择其它模型：例如 `--model insightface-arcface --model-path /srv/picer-models/w600k_r50.onnx`。参数也可由对应 `PICER_*_MODEL_PATH` 及 `PICER_YUNET_MODEL_PATH` 提供。客户端请求的 `modelVersion` 须与已部署模型一致；只验证所选模型，不自动声明其它算法就绪。
- CUDA 可选：在安装命令加入 `--device cuda`，并将 `--venv` 改为独立的 `cuda` 目录。需预先安装与 CUDA 12.4 兼容的 NVIDIA 驱动；脚本安装 cu124 PyTorch 与 GPU ORT，验证真实 GPU 运算，模型没有 CUDA provider 时失败，不静默声称 GPU 成功。它不会安装或升级驱动。Windows 若出现 DLL 加载问题，确认 Microsoft Visual C++ x64 运行库可用。无兼容 GPU 时使用 CPU 环境。

固定依赖基线为 PyTorch **2.6.0**（官方 CPU/cu124 源）、ONNX Runtime **1.20.1**（CPU/GPU 二选一）、NumPy **1.26.4**、OpenCV headless **4.10.0.84**；Python 安装器 pip 固定 **24.3.1**。版本选择依据 [PyTorch 官方历史安装命令](https://pytorch.org/get-started/previous-versions/#v260) 与 [ORT CUDA/cuDNN 兼容矩阵](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html#requirements)：ORT 1.20.x 的 CUDA 12.x/cuDNN 9 与 PyTorch 2.4 及以上兼容，worker 先导入 PyTorch 以加载其 DLL。CPU 环境没有 CUDA PyTorch，不会因机器有显卡而自动使用 GPU。

脚本固定直接依赖，成功后在 venv 内保存 `verified-versions.json` 与包含已解析传递依赖版本的 `installed-versions.txt`；这不是跨平台 wheel 哈希锁或离线安装包。复现到相同平台时应同时保留这些清单与相应 wheel；不同平台要重新验证。安装仅使用官方 PyPI/PyTorch 源，忽略私有 pip 源配置且不输出子进程原始日志，防止其中的凭据泄露。下载失败时检查网络/证书及磁盘，重新执行即可。此脚本仅准备人脸 worker，不会安装 VL/ASR/TTS 上游服务或替代 IndexService 的部署脚本。

## AI 服务路由

`PICER_LANGUAGE_MODEL_ENDPOINT` 配置语言模型服务（训练失败分析使用智谱）；`PICER_VL_MODEL_ENDPOINT` 配置统一平台管理的本地 VL 服务（训练样本视觉筛选为可选增强）。VL 未配置时，向量训练仍可正常提交，不会阻塞任务。可通过 `/api/ai/capabilities` 查看能力状态。

## 原始媒体、预览与流式播放

新通道要求 GUI、源 MediaAgent 和 IndexService 配套升级。Agent 广告 `media.binary.v1`，每次媒体读取使用独立认证 WebSocket，控制连接不承载大文件 Base64。原文件按 offset/count 拉取、单块最多 1 MiB，源端 256/1600 预览与独立限流避免下载挤占所有缩略图通道。

HTTP HEAD 返回长度和不透明租约 ETag；后续 Range 复用同一身份，源代理保持只读共享句柄。源文件变更、旧租约过期或代理重连不能拼接为同一个下载。小 Range 预读限于请求末尾。旧代理不支持新能力时 HEAD 返回 405，客户端可按兼容策略下载；旧整文件协议仍有 64 MiB 限制。新增通道不依赖 GUI 上的 Python、CUDA 或 FFprobe。

服务端/源端原文件与预览各自有界并发；GUI 默认 2 路下载，可选 2～4。播放器访问 GUI loopback 的临时单视频地址，不携带 IndexService 长期令牌。代理在线不等于模型 ready，IndexService `/health` 成功也不等于源文件或某个模型可用。

## 安装后验收与维护

1. 校验软件包摘要，确认实际部署的程序路径和 `/version` 身份。不同组件分别记录来源，不用旧 PID 或程序集通用版本代替二进制校验。
2. 验证健康与能力状态，再用配对固定公钥和有效令牌读取受保护索引、照片/视频缩略图。源代理授权范围内才可读取原始媒体。
3. 使用 `tools/Picer.ServiceProbe` 从实际安装目录加载 Gateway；环境变量为 `PICER_LIVE_SERVICE_URL`、`PICER_LIVE_SERVICE_TOKEN`、`PICER_LIVE_SERVICE_SPKI_PIN`。凭据通过受保护的进程环境提供，不抄到日志、命令历史或仓库。设置 `PICER_REQUIRE_STREAMING=1` 后必须通过视频 HEAD、首尾 Range 和租约验证；`mediaRangesVerified=false` 只表示兼容读取成功。
4. 验证数据库 schema、完整性、外键以及预期记录数量；发生迁移或恢复时与关闭写入者后的备份比较。探针和健康检查不授权扫描全盘、付费分析或清除用户索引。

源媒体仍在原电脑上；仅恢复索引服务无法替代源代理和原文件。维护前同时核对 GUI 自动启动代理、独立代理登录自启、系统服务及外部保活脚本，避免升级期间程序被重新拉起。远端服务故障先保留索引和脱敏日志，区分代理离线、授权路径失效、网络超时与模型失败，再选择对应恢复操作。
