# Picer v1.3.3 安装指南

更新：2026-10-10。本页说明 v1.3.3 的安装方式。正式可下载资产与发布状态以 GitHub Release 为准；发布流程检查安装、升级、卸载及真实服务连接，不代表所有系统环境都已经验收。[v1.1.2](https://github.com/l17728/picer/releases/tag/v1.1.2) 已正式公开，是独立的历史版本；本页新增安装器不是对旧版本资产的追溯承诺。

## 普通 Windows 用户选择哪个安装器

| 资产 | 安装内容与用途 |
| --- | --- |
| Picer-v1.3.3-win-x64-setup.exe | GUI 与同包 MediaAgent；中文当前用户安装，无需管理员权限 |
| Picer.MediaAgent-v1.3.3-win-x64-setup.exe | 仅安装原媒体所在电脑的驻留代理；中文当前用户安装，无需管理员权限 |
| Picer-v1.3.3-win-x64-binary.zip | GUI 解压包，适合手动部署 |
| Picer.MediaAgent-v1.3.3-win-x64-binary.zip | 独立代理解压包 |
| Picer.IndexService-v1.3.3-win-x64-binary.zip / Picer.IndexService-v1.3.3-linux-x64-binary.tar.gz | Windows/Linux 索引服务，管理员手动配置 |
| Picer.ModelServer-v1.3.3-win-x64-binary.zip / Picer.ModelServer-v1.3.3-linux-x64-binary.tar.gz | 模型 API、worker 与 setup-modelserver.py 环境准备脚本 |
| SHA256SUMS.txt | 实际发布资产的完整 SHA-256 校验清单 |

两个安装器都包含自包含 .NET 和应用目录内的 MSVC CRT，不要求用户另外安装 .NET 或 VC++ Redistributable。GUI 内含代理，代理目录下 tools/ffmpeg 提供 ffmpeg.exe、ffprobe.exe 及 LICENSE，独立代理使用同样的相对目录；程序自动发现工具。普通 GUI/代理用户不再需要手动下载视频工具或设置工具路径。

v1.3.3 GUI 同时包含播放代理所需的 ASP.NET Core/Kestrel 库，无需额外安装运行时。远程照片预览、分块下载和所选播放器的流式播放需要升级 GUI、Agent、IndexService；自动模式对识别到的 VLC/mpv 尝试流式，其他播放器默认下载。播放器由用户选择已安装的程序；其编码支持取决于播放器本身。详见[媒体实施记录](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)。

目标系统为常规 Windows 10/11 x64。安装器目前没有代码签名凭据，因此未签名，Windows 可能显示“发布者未知”或安全提示；一键安装不绕过系统保护，应从正式 Release 下载并核对哈希。Windows N/Server 的 Media Foundation 等系统可选组件不随包提供，不能承诺这些特殊系统无需额外系统配置即可具备全部本地媒体能力。

安装器面向当前 Windows 用户；升级保留连接、配对和目录授权配置。卸载只移除程序及程序注册的启动入口，不删除用户图库、索引或用户配置。两个安装器的安装、重复安装升级、卸载及数据保留已在 Release 流水线验证。独立代理默认创建登录自启入口；隔离测试禁用了自启任务，未模拟系统注销再登录。未配置的新代理先展示设置，不因自启自行连接未知服务或授权目录。

安装目录与用户数据目录分开。升级涉及数据库时仍须关闭同库写入者、备份 DB/WAL/SHM，再执行 schema 迁移；自动安装依赖不取消数据库保护要求。下载后按正式 Release 的 SHA256SUMS 校验安装器，不混用旧程序目录中的 DLL。

## GUI 首次使用：不需要编辑 JSON 或环境变量

纯远程 GUI 只连接索引服务，不需要 Python、CUDA 或模型/runtime 包，也不需要用户单独准备 FFmpeg/FFprobe。安装包带代理不表示必须启用代理；GUI 同机自动管理已授权代理也不能解释成 GUI 自身依赖模型执行环境。

从“文件 → 连接索引服务…”进入中文配置：

1. 选择远程模式，填写管理员提供的 HTTPS 服务地址和访问令牌。
2. 点击“验证连接”。程序先只做 TLS 握手获取证书，此时不发送 HTTP 请求或令牌。
3. 程序显示服务地址和证书公钥的 SHA-256 指纹。通过可信渠道与管理员核对，再明确确认；默认选择不信任。
4. 确认后程序固定该证书公钥，禁止验证请求重定向，检查健康和受保护的只读访问。
5. 验证成功后“保存并重启”。令牌由 Windows 当前用户 DPAPI 加密保存；修改地址或令牌后必须重新验证。

显示给用户的 SHA-256 指纹用于核对；配置保存的是协议要求的原始 DER SPKI 公钥，不需要用户抄写几百字节的 Base64。拒绝确认、超时或验证失败不保存新连接。连接验证不启动扫描、模型分析或训练。

未选择模式时界面提示连接入口，不把缺少本地模型当作安装失败。远程模式初始化后不执行通用本地运行组件缺失检查；明确选择本地模式后，本地执行的具体功能仍须相应组件。原始媒体只读约束不变。

## 驻留代理首次使用

在原照片/视频所在电脑安装独立代理，或启用 GUI 包中的代理。使用中文设置填写服务地址、令牌，获取并核对证书指纹，再选择一个或多个允许读取的目录根节点。只有用户允许的 N 个目录及其子树可作为源媒体范围；未配置目录时不自动扫描整盘。

代理通过托盘入口打开设置、重新连接或退出；登录自启沿用已保存授权，新增目录或修改连接由用户操作。令牌按当前用户加密保存，升级继续使用已有配置。包内 FFmpeg/FFprobe 自动发现，无需普通用户设置 PICER_FFMPEG_PATH。已有管理员环境变量配置作为兼容入口，不能覆盖或扩大用户授权范围。

视频处理链路为 GUI → IndexService → 源电脑 MediaAgent → 包内 FFmpeg/FFprobe。代理只转发模型请求时不需要 Python/CUDA；要使用 ASR、VL、人脸、TTS 等能力，管理员仍须配置真实模型端点。代理在线或显示 ready 不等于模型质量验收完成。

## 依赖按运行角色划分

| 角色 | 用户需要准备什么 |
| --- | --- |
| 纯远程 GUI | 管理员提供的服务地址、令牌及可核对的证书指纹 |
| 源媒体 MediaAgent | 相同连接信息及用户选择的允许目录；视频工具由安装包提供 |
| IndexService（代理派发模式） | 服务数据目录、认证/TLS 和连接到媒体代理的配置 |
| ModelServer Python worker | Python、脚本安装的依赖及已获授权的模型权重；CPU 为默认 |
| GPU 模型部署 | 相容 GPU 驱动与 GPU 运行环境由管理员准备，不能由 GUI 安装器代替 |
| 独立 Qwen/ASR/TTS 服务 | 按对应服务准备其模型和执行环境，不包含在 GUI 安装契约中 |

在同一台电脑安装 GUI、代理和模型时会同时出现这些组件，但各自职责仍独立。模型权重体积与授权各异，不把全部模型打入普通 GUI 安装器，也不宣称其完全离线具备任意模型能力。

## 服务端手动安装

IndexService 解压对应 Windows/Linux 二进制包，配置 PICER_INDEX_DATA_ROOT、PICER_INDEX_SERVICE_PORT 和 PICER_INDEX_SERVICE_TOKEN。Windows 启动 Picer.IndexService.exe；Linux 为 Picer.IndexService 设置可执行权限后启动。生产环境配置 HTTPS、访问控制、进程管理及备份。服务端与 GUI 使用同一配对公钥协议，不能为省略证书确认而关闭验证。

ModelServer 的 Windows/Linux 包均携带 setup-modelserver.py、worker 和固定依赖清单。准备 Python 3.10–3.12 x64 与已获授权的模型文件，在解压目录执行以下命令即可建立隔离 venv、安装固定依赖并加载验证识别模型与 YuNet（路径替换为实际模型文件）：

```powershell
python setup-modelserver.py --model sface --model-path "D:\models\sface.onnx" --detector-path "D:\models\yunet.onnx"
```

```bash
python3 setup-modelserver.py --model sface --model-path /srv/models/sface.onnx --detector-path /srv/models/yunet.onnx
```

默认 CPU；需要 GPU 时显式增加 `--device cuda`，需相容 CUDA 12.4 的驱动与可用 GPU。可用 `--venv` 指定独立环境，不在原环境直接切换 CPU/CUDA。Linux Python 需可用的 venv 模块；Windows Python worker 还须满足其原生依赖所需的 VC++ x64 运行库，这属于服务器环境，不是 GUI 用户前置依赖。成功后在启动服务前读取 venv 内的 worker-env.ps1 或 worker-env.sh；`--check` 仅复核已准备环境，不安装依赖、不写配置。`--embeddings-only` 只验证预计算向量校准，不能据此声称图像检测/向量提取已就绪。

脚本不自动安装 GPU 驱动，不下载授权未确认的模型权重。默认安装需要访问 pypi.org 与 download.pytorch.org；完全离线服务器需另行由管理员准备并部署兼容环境，当前命令不提供自动离线依赖安装。模型服务配置 PICER_MODEL_SERVER_API_KEY 和 HTTPS/受控反向代理；准备环境成功后还须启动服务，验证健康及一次实际模型请求。

## 验证与发布边界

最终提交 0d50de4 的 CI 与 Release 均通过严格构建、623 项单元和 86 项 WPF 测试，失败和跳过均为零。配对测试包含真实 TLS 无凭据发现、网关固定公钥调用、DPAPI 保存及重载，以及实际服务模式不触发本地 runtime 提示。两个安装器另行完成安装生命周期验证；安装目录中的通信组件分别成功读取真实照片/视频索引及解码缩略图。完整证据和公开状态见[安装实现记录](INSTALLER_IMPLEMENTATION_2026-10-10.md)。

首次配对与允许目录由隔离 UI/协议测试验证；安装后程序、视频工具及自包含运行环境另有实际运行证据。它们不等同于真实用户注销登录或所有硬件环境验收。构建和外部 CLI 失败须停止；每个上传资产小于 2 GB，完整离线资产仍保留固定哈希及许可证门禁。模型质量、真实场景和硬件验收按各自范围记录。

## v1.1.2 历史发布

v1.1.2 于 2026-10-10 00:45:21 UTC 正式公开，非草稿、非预发布。Release 回归 610/610 单元、73/73 WPF，离线构建通过；当时为六个二进制包、完整 GUI、runtime 共八个包及其他说明/清单，共 13 项资产，摘要校验完成。

旧 v1.1.2 提供解压包而非本页的新 setup.exe；其固定 runtime 只附 ffmpeg.exe，源代理仍需额外 ffprobe.exe。这个旧版限制由 v1.3.3 新代理工具打包契约解决，不能反向声称旧包已经包含。本轮尚未改变 v1.1.2 的发布事实。
