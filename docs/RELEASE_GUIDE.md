# Picer v1.1.2 发布与安装

此文说明发布契约，不代表 GitHub CI、Release 或部署已经成功。以对应提交的 Actions 结果、最终 Release 资产和实际健康检查为准。

## 依赖安装在哪台机器

**仅作为远程客户端使用的 GUI 不需要安装 Python、CUDA、FFmpeg 或 FFprobe，也不需要下载模型/runtime 包。** GUI 经 IndexService 请求媒体处理和模型推理；打包时附带 MediaAgent 不表示每台 GUI 机器都必须启用代理。

| 运行角色 | 所需外部组件 |
| --- | --- |
| 远程 GUI | 服务连接与配对配置；自包含程序包已含 .NET 运行时 |
| IndexService（当前代理派发模式） | 连接源媒体代理和模型服务，不要求本机安装上述推理/媒体工具 |
| MediaAgent（持有原照片/视频的源机器） | 执行视频任务时需要 ffmpeg.exe 和 ffprobe.exe；只转发模型请求，不因转发而需要 Python/CUDA |
| ModelServer 的 Python 人脸推理/训练 worker | Python、对应依赖和模型；CUDA 仅在启用 NVIDIA GPU 推理时需要，worker 也支持 CPU |
| 独立 Qwen/ASR 等模型服务 | 按具体服务安装其 Python/GPU/模型依赖，与远程 GUI 安装分开 |

当前部署把 GUI、MediaAgent 和模型服务放在同一台 Windows 机器，因此依赖共同出现在这台电脑上，但它们属于代理/模型服务。视频探测链路为 GUI → IndexService → 源机器 MediaAgent → FFprobe；模型请求通过 IndexService 路由至代理/模型端点。GUI 不直接启动这些 Python worker 或 FFprobe。

## 下载哪个包

| 资产 | 用途 |
| --- | --- |
| Picer-v1.1.2-win-x64-binary.zip | Windows GUI，内含 media-agent 子目录 |
| Picer.MediaAgent-v1.1.2-win-x64-binary.zip | 独立 Windows 源媒体驻留代理 |
| Picer.IndexService-v1.1.2-win-x64-binary.zip | Windows 索引服务 |
| Picer.IndexService-v1.1.2-linux-x64-binary.tar.gz | Linux 索引/中转服务 |
| Picer.ModelServer-v1.1.2-win-x64-binary.zip | Windows 模型 API 与 Python worker 源码 |
| Picer.ModelServer-v1.1.2-linux-x64-binary.tar.gz | Linux 模型 API 与 Python worker 源码 |
| SHA256SUMS.txt | 二进制及安装说明的校验值 |
| Picer-v1.1.2-win-x64-full.zip | Windows GUI、内置代理、自包含 .NET 与固定运行组件 |
| Picer-runtime-v1.1.2-win-x64.zip / SHA256SUMS-OFFLINE.txt | Windows 离线工具与固定模型及校验清单 |

这些是解压安装包，不是 MSI。二进制包含对应平台的 .NET 运行时及发布输出中的本机库，不包含全部 AI 模型、CUDA、Python 或外部模型服务。不要删除 DLL、runtimes、licenses 等依赖目录。Linux IndexService 主要通过 Windows MediaAgent 执行媒体工作，不能将 Windows 原生视觉库当作 Linux 本机推理能力。

运行时基础资产 runtime-v1/Picer-runtime-win-x64.zip 经固定 SHA-256 和当前逐文件门禁验证后复用，不能只凭文件名或日期推断兼容性。需要本地运行组件的部署使用本 Release 的版本化 runtime 资产；纯远程 GUI 可直接选择 binary 包。完整包不需要再次解压 runtime；额外的代理工具和模型端点仅在对应角色机器上按下文配置。

## 校验和 Windows 安装

先以 Get-FileHash -Algorithm SHA256 核对下载文件与清单。解压到新的安装目录，保留整包结构；不要覆盖正在运行的安装或混用新旧依赖。

将版本化 Windows runtime 包按其目录结构解压到 GUI 安装根的 runtime 子目录，以及需要本地执行的 Windows IndexService 安装根的 runtime 子目录。避免出现 runtime/runtime 双层目录。在 GUI“运行组件”执行完整校验及运行自检，缺失或固定哈希不符必须修复，不能禁用校验。InsightFace/Qwen 等配置的外部模型仍需各自准备和授权。

启动 GUI：Picer.App.exe。升级旧库前关闭全部同库写入进程，完整备份 DB/WAL/SHM；当前 schema 45 升级失败时恢复匹配版本程序和备份，不能手动降低 user_version。

## 独立代理与索引服务

独立代理运行 Picer.MediaAgent.exe。配置进程环境：

- PICER_INDEX_SERVICE_IP：索引服务地址，按服务配对配置填写。
- PICER_INDEX_SERVICE_TOKEN 与 PICER_INDEX_SERVICE_SPKI_PIN：配对令牌和可信公钥指纹，不写入共享脚本或日志。
- PICER_MEDIA_AGENT_ROOTS：允许读取的源根目录列表；分隔方式按当前代理配置，通常使用分号。
- PICER_FFMPEG_PATH：指向同时包含 ffmpeg.exe 与 ffprobe.exe 的可信 FFmpeg 工具目录中的 ffmpeg.exe。本次固定 runtime 只附带 ffmpeg.exe，独立代理的视频探测和能力注册还需另备 ffprobe.exe；GUI 组件自检通过不代表代理媒体依赖齐备。
- PICER_LOCAL_ASR_ENDPOINT、PICER_LOCAL_VL_ENDPOINT、PICER_LOCAL_TTS_ENDPOINT、PICER_LOCAL_FACE_ENDPOINT：实际已启动的模型端点；未配置不等于能力 ready。

GUI 内含的 media-agent 同样需要实际源访问权限和服务配置。代理执行扫描/抽帧不修改原媒体，缓存清除不删除原文件。

IndexService 配置 PICER_INDEX_DATA_ROOT、PICER_INDEX_SERVICE_PORT、PICER_INDEX_SERVICE_TOKEN，Windows 启动 Picer.IndexService.exe；Linux 解压 tar.gz 后先 chmod +x Picer.IndexService，再运行 ./Picer.IndexService。生产环境通过服务管理器启动、配置访问控制与 TLS，不能把服务绑定公网即视为安全安装完成。Linux 配置 Windows MediaAgent 连接和实际模型路由。

## ModelServer 的额外依赖

包内 worker/face_worker.py 与 worker/requirements.txt 随版本发布。requirements 是所需依赖名称清单，不是跨 CPU/CUDA 的可复现锁文件。使用独立 Python 环境安装 torch、numpy、onnxruntime、opencv-python-headless；GPU 部署须根据驱动选择相容 PyTorch/CUDA 和 ONNX Runtime，不能同时盲装互斥 CPU/GPU 发行版。

设置 PICER_GPU_WORKER_PYTHON 为该环境 Python 完整路径，PICER_GPU_WORKER_SCRIPT 为安装目录 worker/face_worker.py，按需配置 PICER_GPU_WORKER_USE_CUDA。模型权重外置并按对应模型环境变量提供，例如 PICER_INSIGHTFACE_MODEL_PATH；不得依赖源码中的开发机默认路径。配置 PICER_MODEL_SERVER_API_KEY，生产环境使用 HTTPS/受控反向代理。Linux 先 chmod +x Picer.ModelServer，再启动；Windows 运行 Picer.ModelServer.exe。

Qwen ASR/VL、TTS 和其他推理服务按部署方案单独安装。ModelServer 二进制和 Python worker 并不包含这些权重或完整 GPU 运行环境。启动 /health 成功只证明 API 可达，还需验证已配置能力和一次真实请求。

## 发布流程与门禁

CI 保留两套非 RuntimeComponent 测试，单用例挂起诊断为 10 分钟、总作业上限 60 分钟，避免真实 100k 规模测试被原 60 秒门限误杀。Release 工作流校验已存在 tag 的提交，严格构建并执行同样的两套测试，再打六个包；命令失败立即停止。

tools/package-release.ps1 是明确的外置运行资产二进制发行模式，仅从新 staging 中移除模型/工具 runtime 目录，不过滤发布所需 DLL 和本机依赖。它不替代 tools/publish-self-contained.ps1；完整离线发行仍须通过后者全部固定大小、哈希及许可证检查。每个上传资产必须小于 2,000,000,000 字节，超限须拆分组件并重新生成清单，不上传截断包。

工作流创建 draft Release，发布负责人补齐通过原固定哈希校验的版本化运行资产、对应清单及安装冒烟证据后再公开。工作流手动重跑要求已有 tag，不自动给任意分支创建版本标签；已有同名 Release 时停止供人工核对，避免悄悄替换已发布资产。

RuntimeComponent 检验和本轮真实部署独立于轻量包 CI。历史本机 605 分轮单元证据与 WPF 73/73 不能替代当前提交的 GitHub CI 结果，模型质量/硬件暂停验收也不因生成安装包自动完成。
