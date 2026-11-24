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

## 对话恢复证据

- `G:\.codex` 的历史备份已扫描；其中未发现可直接恢复的 Picer 分类源码，主要是其他工程的会话记录。
- 当前 Picer 会话记录保留了分类交互要求：左侧分类树、待确认建议和历史分类开关，右侧虚拟化缩略图，滚动到底自动继续加载，勾选后批量确认或拒绝，双击打开预览。
- 因此分类入口必须指向 `ClassificationBrowserWindow`；旧的 `PhotoClassificationWindow` 仅保留为兼容实现，不作为主菜单入口。
