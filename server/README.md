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
