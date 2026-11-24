# 远程模式照片人脸分析与本机训练制品导入实现报告（2026-11-24）

对应[诊断报告](FACE_VIDEO_DATA_LOSS_DIAGNOSIS_2026-11-23.md)的发现一、发现二（缺口①②）。前两项代码缺陷（发现三、四）已由 [VIDEO_PEOPLE_ANALYSIS_FIX_2026-11-23.md](VIDEO_PEOPLE_ANALYSIS_FIX_2026-11-23.md) 修复并于本日部署。

## 缺口①：远程模式下补齐照片人脸

- **现状**：远程（照片空间）模式下 GUI 只创建只读的 `ServiceFaceAnalyzer`，不构造 `VisionAnalysisService`，人物页"检查新照片"不可用；234 张只在 `adaptive` 空间有脸的照片和 388 张从未分析的照片因此无法由用户自助补齐。
- **实现**（`PeopleViewModel.AnalyzeThroughServiceAsync`）：
  - 远程模式下"检查新照片"可用：逐张调用服务的 `face/detect`，**人脸由服务端写入照片空间**，GUI 只记录"该照片已分析"的标记（`VisionAnalysisRepository.SaveServiceCompleted`），不往镜像里写人脸——避免与镜像同步重复写入。
  - 只处理**当前模型空间还没有人脸的照片**（`GetImageIdsWithFaces`），已有分析标记的跳过；所以首次运行只覆盖缺口，之后是增量。
  - 服务器的模型空间是固定的，所选模型空间与之不一致时提示用户先切换，不把脸加到用户看不到的空间。
  - 连续 5 次失败且零成功即中止（避免授权失效/模型离线时产生数千条相同失败）；失败的只记录异常类型名。
  - 完成后触发一次元数据同步，把服务端写入的人脸拉回镜像。
- **授权**：`face/detect` 属于 `analysis.run`（见前一修复），设备需在配对时被授予"允许分析"；由用户点击触发，符合"远程能力需显式授权"的约束。

## 缺口②：把本机训练制品导入照片空间

- **现状**：训练制品/原型/模型设置只存在于本机旧索引库（`<数据根>/picer.db`），该库从未成为空间发布方；制品进入空间的唯一通道是 `faces/training/apply`，但制品不在 GUI 可见的任何列表里。
- **实现**（`PeopleViewModel.ImportLocalTrainingArtifactAsync` + 人物页按钮"导入本机训练制品"）：
  - 以**只读**原生连接打开本机旧索引，读取所选模型空间的活动制品及人物姓名表（`VisionAnalysisRepository.ReadActiveTrainingArtifact`）；不迁移、不加写锁。
  - **安全闸**：制品原型按人物 ID 引用。导入前逐一核对"本机人物 ID→姓名"与照片空间完全一致，任何不一致即阻止（不输出姓名）。
  - 通过既有流程 `CaptureTrainingBaselineAsync` → `ApplyTrainedPrototypesAsync`（`POST api/service/v1/faces/training/apply`，`index.write`）应用到空间，随后同步镜像并刷新制品列表。
  - 镜像同步包含 `face_training_artifacts`/`face_person_prototypes`/`face_person_policies`/`face_model_settings`，因此应用后下拉列表即可显示。

## 测试

- 新增 `RemoteFaceAnalysisTests`：
  - 服务完成标记可跳过重复分析，且不在本地生成人脸行；
  - `GetImageIdsWithFaces` 只含该模型空间的未归档人脸；
  - `ReadActiveTrainingArtifact` 能从另一库只读取出制品与人物姓名，其他模型空间返回空。
- 回归：视觉/训练/镜像差量相关 63/63；完整 E2E 171/171；严格构建 0 警告。

## 服务器部署记录（IndexService，47.103.99.229）

### 发现五（代码缺陷，部署后实测发现）：照片空间没有继承服务级模型配置

- 现象：路由映射修好、代理也在线后，视频人物分析仍返回 `media_agent_offline`。服务端日志 `Remote video people analysis started … agentSpecified=False` 之后无代理可派发；代理日志里没有任务到达的记录，也没有"收到服务端配置"的记录。
- 根因：账号空间模式下每个空间是独立引擎，`SpaceRuntimePool` 以**空间自己的目录**为 `dataRoot` 调用 `IndexServiceHost.Build`，而 `Build` 只在 `dataRoot/service-config.json` 读取模型端点配置。该文件实际在服务根目录（`/var/lib/picer-index/service-config.json`），三个空间目录里都没有。于是空间里的代理注册时收不到 `agent.config`，不会声明 `face.detect`/`face.embed`/`video.people`/`vl.analyze` 等能力，**账号模式下所有依赖代理的分析任务都被报成"代理离线"**（服务端任务日志自 10-10 切换账号拓扑后再无写入）。
- 修复：`IndexServiceHost.Build` 新增 `inheritedServiceConfigPath`；空间没有自己的配置时继承 `AccountStore.ServiceConfigurationPath`（服务根目录的配置），空间自有配置优先，便于将来按空间覆盖。
- 测试：`SpaceServiceConfigurationTests` 三项——空间继承服务级配置、空间自有配置优先、两者皆无时不回放。

### 部署过程

- 首次部署（提交 `5505e12` 的包）在"结构准备"步骤失败并自动回滚，服务无中断数据损失：该服务实际通过 systemd 单元 `docker run … ubuntu:24.04` 运行在容器内（主机 glibc 2.32 < 原生库要求的 2.34），而安装脚本假定服务直接运行在主机上，且健康探测从单元 `Environment` 读取公网域名（docker 单元用 `-e` 传参）。
- 修复安装脚本（提交 `9c9d9aa`）：从单元 `ExecStart` 识别容器化部署，结构准备在同一镜像与挂载内执行，公网域名回退到容器参数；对 `ExecStart` 做字符白名单校验后再求值。新增 `tests/deployment/test_deploy_container_detection.py`（3 项，CI `deployment-safety` 任务执行）。同时修复 PowerShell 包装脚本在 PowerShell 5.1 下 `$PSScriptRoot` 默认值为空的问题。
- 第二次部署成功：`deploymentId=20261124T013648093Z`，`binarySha256=27bc1bbb9dc58504ab719d8967e7e58d20416477a4ee50ead18dfab89f223c27`，`sourceCommit=9c9d9aa4d12d`；结构版本 v94→v94 无迁移；经公网域名复核 `/version`、`/health` 正常，MediaAgent 与 GUI 自动重连。

## 实测验证与后续修复（2026-11-24 下午）

上面的实现在本机真实环境里逐项实测，每一项都是先跑通再记录。实测又暴露出若干缺陷，按发现的顺序记录如下。

### 前置：本机人脸推理环境重建

- 现象：`picer-model` 的 `/api/face-inference` 自 11-20 起全部返回 500。原因是 Python 被重装到 `G:\AI\python`（裸解释器），原 `C:\Python312` 及其依赖包随之消失，`PICER_GPU_WORKER_PYTHON` 指向不存在的路径。
- 处理：用 `server\setup-modelserver.py --device cuda` 在 `%LOCALAPPDATA%\PicerModelWorker\cuda` 重建（torch 2.6.0+cu124、onnxruntime-gpu 1.20.2），`--check` 通过（实际加载 InsightFace 与 YuNet 并在 GPU 上运行）；启动器改指新环境后重启 `picer-model`。依赖锁定同步修正（`onnxruntime-gpu` 1.20.1 从未发布 GPU 包，改 1.20.2，提交 `1bef267`）。下载镜像与断点续传的注意事项见 [ENGINEERING_MEMORY.md](ENGINEERING_MEMORY.md)。

### 视频人物分析复验

- DXXT7429（id 7）、ARUA4227（id 2）、HLAO8549（id 8）重新分析：服务端日志均为 `Remote video people analysis indexed; faces=1`，空间权威库对应记录的时间戳更新为当日；镜像投影 `videoFaces` 由 6 变为 9（原有 6 条 legacy + 新增 3 条）；DXXT7429 在 GUI 上为 `displayedFaces=1`（此前为 0）。

### 缺口②实测

- **发现六：导入按钮始终禁用。** `ImportLocalTrainingArtifactCommand` 的可用性只随 `IsAnalyzing` 刷新，而页面是在加载完成后才设定所选模型空间，此时不通知命令，按钮一直是灰的（提交 `de7dc30` 引入的功能实际点不到）。修复见 `6b0d689`。
- **发现七：导入只带了原型，没带校准阈值。** 自动归类取不到阈值时回落到默认 0.55，而本机为 0.415，照片空间的归类因此比原来更严。新增授权路由 `POST api/service/v1/faces/model-settings`（`index.write`；校验取值范围、NaN、负样本数；只读设备 403），导入时随制品一并写入。修复见 `15f87f7`。`dd39f8b` 把阈值存储提到应用制品之前，省掉紧随其后的一次全量镜像同步；**实测总耗时并未下降**（25 分钟，此前一次为 21 分钟），耗时由同步本身决定且波动更大。
- **结果**：空间权威库 `face_training_artifacts` 1 条 active、8 个原型，`face_model_settings` 为 0.415（20 万对样本）；镜像库同样有阈值与制品；GUI 训练历史由 `local=0; remote=0` 变为 `local=1`。

### 缺口①实测：照片人脸分析此前从未跑通

"检查新照片"点击后连续失败。逐环实测定位出**四处互相独立的缺陷**，任何一处都足以让这条链路失败：

- **发现八（服务端）：任务路径没有规范化。** `face/{operation}` 是唯一把 GUI 请求体原样转发给代理的端点，其余 24 处发给代理的路径都经过 `IndexApiHandlers.NormalizeAgentPath`。GUI 手里是发布时的逻辑路径 `\\Picer-<发布者id>\G$\…`，在任何机器上都不存在。修复见 `956b591`（新增 `NormalizeFaceTaskPath`）。
- **发现九（代理）：没有把路径换成图片。** `face.*` 任务把载荷原样发给模型接口，而接口只接受 `imageBase64`，每次都是 HTTP 400。新增 `FaceImagePayload`（在授权根目录内读出文件并编码；管理共享形式的路径先映射），`video.people` 与 `vl.analyze` 原本就走这一步，唯独照片任务漏了。修复见 `792c137`。
- **发现十（GUI）：解析服务端应答时区分大小写。** `ServiceFaceAnalyzer` 用默认选项反序列化，服务端输出驼峰命名，字段全部读成空，合法结果被判为 `face result schema invalid`。修复见 `9e40004`。
- **发现十一（代理）：任务异常被吞。** 任务处理只捕获 IO、未授权、无效操作三类异常，而 `InvalidDataException` 直接继承自 `SystemException`；文件缺失时任务静默结束，不回结果、不记日志，GUI 每张要等满 5 分钟超时。现归入会回传 `task.error` 的类型，并对其它意外异常记 `task crashed`，成功路径也补了日志。修复见 `792c137`。
- **定位过程中的两次误判（如实记录）**：先以为代理继承了命令行的代理环境变量，核对后环境里只有 `NO_PROXY`，到 2080 的连接是代理连云端服务的通道；又把镜像里的路径误读成命名共享，实际是 `G$` 管理共享形式（Windows 的 `splitdrive` 把 `\\host\G$` 整体算盘符）。两次都是靠直接测量（TCP 连接、系统调用计时）否定的，不是靠读代码。

### 缺口①实测结果

- 16:12 起在真实照片库上运行：每张由代理在 0.6～2 秒内完成，检出人脸时返回约 1.1 万字符（含特征向量），服务端新增人脸落库；到 16:30 成功 113 张、失败 0。
- **速度**：113 张平均 8.4 秒/张，其中前两分钟是上述队列占用，16:15–16:30 又与完整构建、完整 E2E 并行，有资源争用。无争用时观察到约 4 秒/张。据此估计剩余约 4～8 小时，取决于机器负载。
- **已知限制**：
  - 超过 10 MiB 的照片被拒绝（待分析的 3386 张里有 5 张），记为失败，不计入"连续失败"中止条件；
  - 人物页加载时触发的"统一基线评估集发布"占住数据库队列，首轮保存排队约 2 分钟（一次性，之后每张排队 2～3 秒）；
  - 人脸推理链路没有超时（模型服务持有无超时的单并发信号量，代理的模型客户端也是无限超时），目前只靠 GUI 侧 5 分钟兜底，未修。

## 测试与构建记录

- **完整单元套件**（`1bef267`，11:48–15:22）：**1273 / 1273 通过，0 失败，0 跳过**，3.55 小时。日志 `artifacts/test-logs/picer-tests-20261124-114824.out.log`。
- **此后改动的针对性测试**（Debug，`--warnaserror` 0 警告 0 错误）：
  - 阈值导入：`RemoteFaceAnalysisTests`、`SpaceRoutePermissionTests`、`SpacePublisherTrainingTests`，27/27（新增未校准/已校准读取、路由映射、端点往返与 400/403）；
  - 代理读图：`FaceImagePayloadTests` 11/11（路径替换、已带图片不读文件、共享路径映射、解析结果越界拒绝、缺失/空/超限文件）；
  - 任务路径与视频回归：`GatewayContractTests` 3 项新增（路径还原、无逻辑路径时原样返回、注册代理走通端点并把人脸写入索引），连同既有视频人物分析用例 17/17；
  - 应答解析：`FaceServiceContractTests` 新增 2 项（驼峰应答、模型不符拒绝）。
- **完整 E2E**（最终代码 `9e40004`，16:17–16:29）：**171 / 171 通过，0 失败，0 跳过**；整个解决方案 `dotnet build picer.sln -c Debug --warnaserror` 0 警告 0 错误。运行期间正式 GUI 正在后台分析照片，E2E 只按自己启动的进程号定位窗口，两者互不影响。
- **Release 构建**（`src\Picer.App\bin\Release\net8.0-windows\win-x64\`）：`Picer.App.dll` SHA-256 `b2690d748a71a52cc6fc99e8151c31133b0d8f35330c8c6e4ebd1eb7691e831b`，`Picer.Core.dll` `390aec8c4f63842beb7828190d2e6660997b7c960d745560f85dea50b13159ad`（`9e40004`）；代理 `src\Picer.MediaAgent\bin\Release\net8.0-windows\Picer.MediaAgent.dll` `531dfa5e8aa84aaea385caa295474eb1b2eda4b99dd8c049bdac4bd7bf359b0e`（`792c137`）。

### 部署记录（续）

| deploymentId | sourceCommit | binarySha256 | 内容 |
|---|---|---|---|
| `20261124T054607774Z` | `145bc7704172` | `b70fbbe3959a1e5df59904a265a1b77771d04fa8703aaaf72759b357c1062b4c` | `faces/model-settings` 路由 |
| `20261124T080227209Z` | `792c13758179` | `d12a4449fb0342864d4e7d5b7a97c9240b75824a069802693061f9ca7ad0c953` | 照片任务路径规范化 |

两次均经安装脚本部署（自动备份、健康验证通过），MediaAgent 在服务重启后自动重连并重新收到配置。

## 用户操作

1. ~~重新配对 GUI 设备并勾选"允许分析"~~ 已核实本机 GUI 设备已有 `analysis.run`，无需重新配对。
2. ~~"导入本机训练制品"~~ 已于 13:36（制品）与 13:51（阈值）执行完成。
3. **"检查新照片"**：16:12 起已在运行（平均 8.4 秒/张，剩余约 4～8 小时）。人脸在服务端写入，GUI 界面要等下一次镜像同步才显示。需要停下时点人物页的"取消"；之后再次点击会跳过已有分析标记的照片，从断点继续。
4. 视频页 → 对需要的视频重新点击"分析视频人物"（DXXT7429、ARUA4227、HLAO8549 已完成）。
