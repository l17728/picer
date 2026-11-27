# Picer 工程记忆：提交、构建与 GUI 基线

## 必须遵守

1. 每完成一个可验证功能，立即创建 Conventional Commit 并推送到 `origin/main`。
2. 正式 GUI 布局必须有稳定标签或基线提交；临时 UI 只能放在独立分支，不能接入正式菜单或快捷方式。
3. 所有新增源码必须先确认已被 Git 跟踪；未跟踪源码不得作为正式功能交付。
4. 构建产物必须记录 commit、完整路径、构建配置和 SHA-256；运行验收必须核对进程路径。
5. 源码出现编码损坏、构建异常或 UI 异常时，先停止继续改写，优先从 Git、标签或备份恢复。
6. 每个阶段都要在干净 Release 构建上启动验收，并确认唯一快捷方式指向该构建。
7. 删除或替换 GUI 前，先保留可回滚基线；确认新版本功能后再清理旧入口。

## 推送与网络

- 推送 `origin`（`https://github.com/l17728/picer.git`）直连超时或被重置时，走 **GitHub 备用地址 `140.82.112.3`**。不要改 `origin`，不要改系统 hosts，不要关闭证书校验：
  ```powershell
  git -c http.curloptResolve=github.com:443:140.82.112.3 push origin main
  ```
  域名、SNI 与证书保持不变，仅把这一次连接解析到备用 IP（需 Git ≥ 2.37，本机 2.56）。推送失败时先用直连重试 2～3 次，仍失败再走备用地址；备用路线的推送也可能偶发被重置（2026-11-24 实测：只读 `ls-remote` 稳定，推送两次被重置后由直连成功），两条路线**交替重试**即可，不要因此改配置。

## 生产环境拓扑（2026-11-24 实测核对）

- **IndexService 在云服务器 `47.103.99.229`**（`https://47-103-99-229.sslip.io:8445`），不在本机。本机 `~/.ssh/id_ed25519` 可免密登录 `root@47.103.99.229`。
- 该服务**跑在 Docker 容器里**：systemd 单元 `picer-index.service` 的 `ExecStart` 是 `docker run … ubuntu:24.04 /opt/picer-index/Picer.IndexService`（主机 glibc 2.32 低于自带原生库要求的 2.34，所以必须进容器）。安装目录 `/opt/picer-index`，数据根 `/var/lib/picer-index`（账号库在其下 `accounts-v2/spaces/<空间ID>/`），服务级模型端点配置 `/var/lib/picer-index/service-config.json`，应用日志 `/var/lib/picer-index/logs/`。
- 部署命令（安装脚本已支持容器化单元，自带备份与失败自动回滚）：
  ```powershell
  $env:PATH = "C:\Users\a\.dotnet;" + $env:PATH   # PATH 上的 dotnet 没有 SDK
  powershell -ExecutionPolicy Bypass -File tools\deploy-indexservice-linux.ps1 -Server 47.103.99.229 -User root
  ```
  发布阶段读取工作区源码，**部署前必须先把未验证的改动 `git stash`**，保证部署包来自已提交的版本。部署会重启服务，GUI 与 MediaAgent 会自动重连。
- **本机 MediaAgent**：由启动文件夹的 `Picer 媒体驻留代理.lnk` 直接拉起 `src\Picer.MediaAgent\bin\Release\net8.0-windows\Picer.MediaAgent.exe`（无参数，使用保存的设备凭据）。连接日志在 `%TEMP%\picer-media-agent.log`。它的模型端点来自服务端下发的 `agent.config`，本机不再单独配置。
- **本机模型服务**：控制台 `D:\VL-models\manager`（`node server.js`，监听 8090），启动器 `D:\VL-models\start-model-console.ps1`；开机自启项为启动文件夹里的 `model-console-startup.vbs`，桌面有"本地模型服务"快捷方式。人脸推理/训练服务 `picer-model` 在 8091（`D:\VL-models\start-picer-model-service.ps1`）。
- **人脸 worker 的 Python 环境**：`picer-model` 的 `/api/face-inference` 靠 `PICER_GPU_WORKER_PYTHON` 启动 Python worker（`face_worker.py`）。2026-11-20 Python 被重装到 `G:\AI\python\cpython-3.12.16-windows-x86_64-none`（裸解释器，无依赖包），原 `C:\Python312` 连同依赖消失，人脸推理自此全部 500。2026-11-24 用官方脚本重建：`python server\setup-modelserver.py --device cuda --model insightface-arcface --model-path D:\vl-models\models\insightface\w600k_r50.onnx --detector-path D:\picer\third_party\runtime\models\face\face_detection_yunet_2023mar.onnx`，环境在 `%LOCALAPPDATA%\PicerModelWorker\cuda`（torch 2.6.0+cu124、onnxruntime-gpu 1.20.2），`--check` 已通过（模型加载 + GPU 运行验证）。启动器里的 `PICER_GPU_WORKER_PYTHON` 已指向 `…\PicerModelWorker\cuda\Scripts\python.exe`。
- **重建该环境的网络要点**（2026-11-24 实测）：`download.pytorch.org` 经代理返回 403、直连超时；PyTorch 需从国内镜像直接下载 wheel（上交镜像 `https://mirror.sjtu.edu.cn/pytorch-wheels/cu124/` 约 1.6 MB/s，比阿里云快 8 倍；阿里云/上交的页面不是 pip 认的索引格式，要用 curl 下载后 `pip install <wheel>`）；其余依赖走 `https://mirrors.aliyun.com/pypi/simple/`。**不要跨镜像续传同一个文件，开始前先确认没有旧的 curl 进程还在写**（两个进程同时追加会产生超大的损坏文件）。下载后核对大小（torch 2.6.0+cu124 cp312 win_amd64 = 2,532,302,369 字节）与 zip 完整性。
- **MediaAgent 排障要点**（2026-11-24 实测）：调用方长时间没有响应时，先看 `%TEMP%\picer-media-agent.log` 里 `task accepted` 之后有没有下文。自 `792c137` 起，任务里未预期的异常类型会记一行 `task crashed; …; error=<类型>`；在此之前 `InvalidDataException`（文件缺失、尺寸非法等）既不回传也不记录，任务静默结束，调用方只能等到自己超时——照片人脸分析"每张卡 5 分钟"就是这样来的。重建代理：`Stop-Process` → `dotnet build src/Picer.MediaAgent/Picer.MediaAgent.csproj -c Release` → 直接启动 `bin\Release\net8.0-windows\Picer.MediaAgent.exe`（无参数），约 6 秒重连并重新收到 `agent.config`。代理设置在 `%LOCALAPPDATA%\Picer\media-agent\settings.json`：普通 JSON，`Roots` 可直接读，令牌字段是 DPAPI 密文，不要输出。
- **路径的三种形态**（排查"文件找不到"必看）：照片库真实路径形如 `G:\Apple照片\Apple照片\…`（代理授权根目录是 `G:\Apple照片`）；照片空间镜像库里 `images.path` 是发布时的逻辑路径 `\\Picer-<发布者id>\G$\Apple照片\Apple照片\…`，在任何机器上都不存在；服务端发给代理前必须经 `IndexApiHandlers.NormalizeAgentPath` 还原。GUI 远程模式手里只有逻辑路径，所以凡是把 GUI 上传的路径转给代理的任务，都要在服务端规范化（`face/{operation}` 在 `956b591` 补上，此前直接原样转发）。另外 Windows 的 `splitdrive` 把 `\\host\G$` 整体算作盘符，用 Python 拆路径时容易看错。
- **完整单元套件的日志**：`tools/run-picer-tests.ps1` 的完整输出在 `artifacts\test-logs\picer-tests-<时间戳>.out.log`。后台任务的进度流每 30 秒只留一行，看不到最终汇总，通过/失败数要去这个文件里读。
- **人脸推理链路没有超时**（已知未修）：模型服务 `GpuEmbeddingService.ExecuteAsync` 持有单并发信号量且不带超时，worker 一旦不回应，之后所有推理请求都会排队；代理的模型客户端也是无限超时。目前只有 GUI 侧 `ServiceFaceAnalyzer` 的 5 分钟超时兜底。
- **系统代理**：Shadowsocks 客户端监听 `localhost:2080` 并自动设置系统代理；若它没运行而系统代理仍指向 2080，浏览器会报"无法连接到互联网"，而 dotnet 等不走系统代理的程序正常——此时关闭系统代理或启动客户端即可，不要误判为网络故障。
- **pip 不走系统代理**：本机设置了 `NO_PROXY`，Python 因此忽略注册表里的系统代理而直连，下载 PyTorch 等大文件会卡死。装 Python 依赖时显式设置 `$env:HTTPS_PROXY = 'http://127.0.0.1:2080'`（`server\setup-modelserver.py` 会保留该变量）。

## 本次事故记录

- 未跟踪的分类窗口源码被重建并接入入口，造成简版 UI 覆盖正式窗口。
- 多个历史构建目录和快捷方式并存，导致实际运行版本难以确认。
- 业务修复曾长期积累后才集中提交，增加了恢复和核对成本。

## 当前基线

- 主页面完整布局恢复提交：`14ca4f2`
- 一键配置菜单恢复提交：`34d791f`
- 分类浏览器当前基线：以 `e52f416` 的左树右图重构为基础，并在工作区补充分页游标、滚动自动加载和批量审核入口
- 业务修复汇总提交：`0e486e7`
- 当前正式 GUI：`src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe`
- 2026-11-26 人物确认修复构建：commit `97fb946594902b995590391f029597f0eef8988d`，`src/Picer.App/artifacts/builds/video-face-confirmation-2026-11-26/Release/net8.0-windows/win-x64/Picer.App.exe`，SHA-256 `DD47711C8539536885DC9E30B3FD84370266EC197A8CFA12E2303B29157DB030`。该侧载构建当前运行 PID 3296，窗口标题已确认远端 `47-103-99-229.sslip.io:8445`，可见窗口句柄非零。

## 对话恢复证据

- `G:\.codex` 的历史备份已扫描；其中未发现可直接恢复的 Picer 分类源码，主要是其他工程的会话记录。
- 当前 Picer 会话记录保留了分类交互要求：左侧分类树、待确认建议和历史分类开关，右侧虚拟化缩略图，滚动到底自动继续加载，勾选后批量确认或拒绝，双击打开预览。
- 因此分类入口必须指向 `ClassificationBrowserWindow`；旧的 `PhotoClassificationWindow` 仅保留为兼容实现，不作为主菜单入口。

### 2026-11-26 GUI 远程媒体缓存加密

ServiceMode 的共享远程缩略图、人脸裁剪、视频关键帧和视频摘要拼图现使用 Windows DPAPI CurrentUser 稳定密钥和 PICERENC2 认证加密；旧 JPEG 缓存后台/按需迁移。视频摘要模型调用期间在系统临时目录短暂保存明文，正常结束、失败、取消后删除。提交 ccb74c2 已推送 origin/main；Release 路径 D:\picer\src\Picer.App\artifacts\builds\remote-summary-cache-2026-11-26\Release\net8.0-windows\win-x64\Picer.App.exe，SHA-256 9F7E20E285315343F4FB52DDE6E4E33270CE30A3BEEAA9AEDCD7196FBC4EDCBA。当前 PID 25492 已启动且响应，标题指向 47-103-99-229.sslip.io:8445。既有视频摘要拼图 529 个已迁移、0 失败。Relay SDD 仍未全部闭合；详细范围见 docs/CLIENT_MEDIA_CACHE_ENCRYPTION_2026-11-26.md。


## 2026-11-26 旧关键帧缓存启动迁移

- 提交 `7b0a7bd fix: migrate legacy keyframes after remote startup` 修复远程启动时旧视频关键帧缓存迁移：确保远程缓存管理器初始化后执行迁移，处理完后清理旧目录。
- Debug 整解方案严格构建 0 警告/错误；`SharedImageAssetsTests` 5/5 通过。
- Release GUI 路径 `src/Picer.App/artifacts/builds/legacy-keyframe-startup-migration-final-2026-11-26/Release/net8.0-windows/win-x64/Picer.App.exe`，SHA-256 `EE8955B8FAFFB7741A88C20D3457B787263CCBB5ADAFD99B3984F5B17226A052`；`Picer.App.dll` SHA-256 `7E34B8A70190C0D3805A24DBE55294A73EE3670DC3AFC4391E9986065FE2D761`。
- 实际日志 `migrated=482; discarded=194; failed=0`，旧目录 JPG 计数为 0。Release GUI PID `22756` 正在运行，窗口标题指向 `47-103-99-229.sslip.io:8445`。
- 当前生产数据面仍是按请求 account relay 与独立心跳控制 socket；本机的加密帧/请求多路复用原语尚未接入云端双向转发，GUI 路由、Web 管理和 Relay E2E 仍未全部完成。


## 2026-11-26 Relay 连接身份与控制通道

- 提交 `25db71d feat: add index relay connector identity` 为 `index-service` 新增所有者批准的设备类型，仅授予 `relay.connect`。云端持久 WebSocket 专用 grant 不启动租户 IndexService 运行时。
- Debug 整解方案严格构建 0 警告/错误；Relay connector 与身份相关测试 41/41；详细范围见 `docs/LOCAL_INDEX_RELAY_CONNECTOR_IDENTITY_2026-11-26.md`。
- IndexService Release 路径 `server/Picer.IndexService/bin/Release/net8.0/Picer.IndexService.dll`，SHA-256 `D40582CBC475E06274E122C1267E5DA9FE7C1026941918A2C7C7575E3D4F0CA2`。此构建未部署生产；当时尚无本机凭据存储和重连 worker。
- RELAY-01 至 RELAY-40 尚未全部验收；请求多路复用、密文 payload、GUI 路由、恢复和管理控制台仍待实现。

### 2026-11-26 Local Relay Connector runtime

- 本地 IndexService 已实现持久化 Relay 凭据、设备公私钥绑定校验、Unix 0700/0600 权限、配对导入 API 和单一后台 WebSocket 连接器。连接器使用证书 SPKI pin/DPoP，15 秒心跳及退避重连；状态可从 bearer-token 保护的 `/api/index-relay/v1/status` 查询。缺少凭据时保持禁用，隔离租户服务不启动连接器。
- 验证：Debug 整解方案严格构建 0 警告/错误；凭据与 connector 端到端测试 4/4；IndexService Release 0 警告/错误。正式产物路径 `server/Picer.IndexService/bin/Release/net8.0/Picer.IndexService.dll`，SHA-256 `F4F73443837A1F33AA223D02CBE76662A56574E7BBF3C731A4F4A7B08E2150DD`。
- 尚无索引请求多路复用、数据 payload 加密、GUI 路由和生产服务配对入口。当前生产服务未部署此构建（只有心跳协议，未承载查询），不能称 Relay 迁移或 RELAY-01 至 RELAY-40 完成。详细记录：`docs/LOCAL_INDEX_RELAY_CONNECTOR_RUNTIME_2026-11-26.md`。

### 2026-11-26 Relay 加密协议原语

- Core 新增设备签名临时 ECDH、方向分离 HKDF-SHA256/AES-256-GCM 会话密钥、绑定 tenant/device/session/request/idempotency 的密文帧及序号滑动重放窗口。peer 签名公钥必须来自已认证的账号设备目录；帧正文/路径不应明文发送。
- 新增 `RelayRequestMultiplexer`，为共享 session 上的加密请求做 request ID/幂等键响应关联，默认并行上限 32、最大 256；超时/取消清理 pending 并尽力发送取消帧。
- 新增受限二进制 WebSocket wire envelope，承载签名密钥交换和密文帧，最大 3 MiB，分片读取、串行发送。
- 验证：整解方案严格 Debug 构建 0 警告/错误；Relay 凭据/connector/加密协议/多路复用/wire 测试 11/11；IndexService Release SHA-256 `E4FE9A7D1F25E8EB292D6E086C3A46CF952B25E42BA742C093A1F40BEEF7249B`。
- 原语尚未接入生产 Relay socket；peer key 发现、云路由/分块流、GUI 与本机服务数据流、持久写幂等和负载/撤销验收仍缺。未部署生产。详细边界见 `docs/LOCAL_INDEX_RELAY_PROTOCOL_V1_2026-11-26.md`。

### 2026-11-27 分类标签树与增量计划

- 分类预设包含 230 个节点、212 个可赋标签和 289 条导航边；照片支持多标签，树节点只显示有照片的类别并显示去重计数。详情见 `docs/PHOTO_CLASSIFICATION_TAG_COVERAGE_2026-11-27.md`。
- 增量计划曾一次性保留全部照片上下文并触发 1.8 GiB 服务器的 OOM。提交 `83e5053` 改为 200 张分页计算，计划只持久化照片 ID/指纹/原因；任务启动时按 100 张分页重建并核验输入。35 项分类生命周期测试通过。
- IndexService 已部署到 `47.103.99.229`，部署号 `20261127T091020602Z`，SHA-256 `37717a1ac859942679c4f5819d08a6b26c9428a15169af6ae60d77954ae6c86f`。
- GUI Release 构建位于 `src/Picer.App/artifacts/builds/classification-tree-counts-memory-fix-2026-11-27/Picer.App.exe`，SHA-256 `A8591586B0078B4A20E0A81579444F6B44E076A9550DEED281DED44910590E19`。当时 Windows 处于锁屏状态，未启动该构建；请勿将旧版运行窗口误认为此构建。
- 后续运行发现模型 JSON/证据格式错误，`fc86b4c` 将这些输出校验错误改为受 3 次尝试与调用预算约束的重试；该修复部署号 `20261127T093743195Z`，SHA-256 `c64e6627ac9483c07adcc4876e0e00aaf268dc4c03cfba5b4d382f6128061e81`。锁屏解除前不要尝试自动启动或切换 GUI。

- 2026-11-27 后续收尾：Retry 修复部署后分类任务继续运行，最新观测为 8 成功、4 失败、8,590 待处理、1 运行中。独立发布的 GUI 位于 `src/Picer.App/artifacts/builds/classification-tree-retry-2026-11-27/publish/Picer.App.exe`，SHA-256 `15E5E5D90AAD49ADFF862622E3D9F3E786942C43EC308BFE35042F0BA5B1F189`；窗口启动/视觉验收仍待解锁桌面后完成。
