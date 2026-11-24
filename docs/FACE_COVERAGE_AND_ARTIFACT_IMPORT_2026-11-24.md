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

## 用户操作（功能生效所需）

1. **重新配对 GUI 设备**并在 Web 审批页勾选"允许分析"（缺口①需要 `analysis.run`）。
2. 人物页 → 选择 `insightface-arcface` 模型空间 → 点击**"导入本机训练制品"**（缺口②，只需编辑权限）。
3. 人物页 → 点击**"检查新照片"**（缺口①）。
4. 视频页 → 对需要的视频重新点击"分析视频人物"。
