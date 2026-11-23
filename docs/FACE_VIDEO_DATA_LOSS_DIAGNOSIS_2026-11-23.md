# 人物与视频数据缺失诊断报告（2026-11-23）

## 背景

用户报告三类现象并要求定位：① IMG_9965 等照片详情无人物信息；② 人物页"模型历史制品"下拉为空；③ 视频分析状态标注"人物已完成"，但"全部人脸"为空（以 DXXT7429.mp4 为例）。本报告为纯诊断：全部查询只读（通过 `winsqlite3.dll` 对本地/镜像 SQLite 只读访问），除一次授权的复现实验（对 video=7 触发远程人物分析）外未修改任何数据，未修改任何代码。

数据源：GUI 当前为远程（账号空间）模式，读取 `D:\PicerData\app-data\spaces\dac4b7e1…\remote-mirror.db`（下称"空间镜像"）；对照 `D:\PicerData\app-data\picer.db`（本地索引库）与 `D:\PicerData\app-data\remote-mirror.db`（10-15 之前的旧远程镜像）。

## 发现一：人脸数据分裂在三个模型空间，当前空间不覆盖全部照片

- 库中共三个互不相通的人脸模型空间：
  - `yunet+sface`：10000 张人脸（08-28~09-01）；
  - `adaptive:…`：872 张（仅 09-15 一天，覆盖 439 张照片）；
  - `insightface-arcface`：10464 张（09-18~09-20）。
- 当前人物页使用 `insightface-arcface`。IMG_9965（imageId 3034）的人脸只在 `adaptive` 空间（2 张，置信度 0.92/0.84），当前空间为 0 → 详情无人物。
- insightface 空间的人脸并非常规分析产生，而是 `BackfillInsightFaceAsync`（"补齐 InsightFace 向量"）从**单一来源空间**换算：来源取人脸最多的空间（`PeopleViewModel` 按数量降序选择），当时为 `yunet+sface`（10000）。**只在 adaptive 空间有脸的 234 张照片（全部 08-27 入库，含 IMG_9965）因此被漏掉**。设计局限，不是常规分析路径的 bug（`VisionAnalysisService.IsCurrent` 本身按模型空间判定）。
- 388 张照片在最后一次人脸分析（09-20）之后入库，任何空间都没有人脸。
- 远程模式下 GUI 不能发起照片人脸分析：`AppServices` 远程分支只创建只读的 `ServiceFaceAnalyzer`（"No face inference runs here"），`_analysisService` 为 null，"分析全部"不可用。此缺口在当前部署下无法由用户自助补齐。
- 人物页归属现状：当前空间 5217 张照片有脸，其中 3398 张有人物归属；另有 10442 张人脸被标记"不分类"（`face_review_states.ignored`）。

## 发现二：制品下拉为空——模型层从未进入空间

- 下拉两个来源：本地库 `face_training_artifacts`（准确率 ≥0.90）与训练平台历史。日志确认两次加载均为 `local=0; remote=0`。
- 空间镜像中整个人脸模型层为空：`face_training_artifacts` 0、`face_model_settings` 0、`face_person_prototypes` 0、`face_person_policies` 0。
- 本地索引库有完整模型层：制品 1（insightface-arcface，active，误拒率 0.03）、模型设置 2、原型 16。但该库无 `sync_publishers` 表——**从未成为空间发布方**。
- 机制（由 `SpacePublisherTrainingTests.RemoteTrainingApplicationRoundTripsThroughAuthenticatedApi` 证实）：制品进入空间的唯一通道是"已配对发布器（上传范围 People:true）执行 `ApplyAgentTrainingArtifact` 后 `PublishAsync`，再经 `POST api/service/v1/faces/training/apply` 在空间侧应用"。制品于 09-20 应用在本地索引，此后本机切换服务模式，模型层留在旧位置。
- 关联症状：日志多次出现"统一基线评估集上传失败：评估快照没有可上传的人脸"。

## 发现三（代码缺陷）：关键帧重新生成会级联删除人脸且不重置分析状态

- 证据链：旧远程镜像（10-15 前）中 ARUA4227/DXXT7429/HLAO8549 三个视频状态 `completed` 且**有人脸**（1/1/2 张，挂在 09-22 生成的关键帧上）；空间镜像中同一批视频的关键帧是 **10-20/10-27 重新生成的全新编号**（731/737/738），人脸为 0，状态仍 `completed`。全库视频人脸从 26 张降为 6 张（另有 service-face 16 张同样消失）。
- 代码：`IndexServiceHost.SaveGeneratedKeyframes` 与 `VideoIndexService.SaveKeyframes` 均 `DELETE FROM video_keyframes WHERE video_id=…` 后重插；`video_detected_faces.keyframe_id` 外键 `ON DELETE CASCADE` → 人脸随之删除；`videos.person_analysis_status` **不重置**。UI 的"全部人脸"直接读该表，于是显示 0。
- 复现实验（授权）：在远程模式下对 video=7 触发"分析视频人物"，得到 `HTTP 403`（见发现四），无法走通。

## 发现四（代码缺陷，新发现）：账号服务路由映射缺失导致"分析视频人物"永远 403

- `AccountServiceHost.SpaceRoutePermissions.RequiredScope` 对 `POST /api/service/v1/video-people/{id}` 无映射：`serviceParts[3]` 分支只列了 `video-keyframes`/`video-transcribe`（`AccountServiceHost.cs:296`），其余 POST 落到 `return null`（:318），中间件对 `scope is null` 一律 403。
- 同样未映射的还有 `/api/service/v1/face/{operation}`、`/api/service/v1/vl/stream`、`/api/service/v1/training/api/face-training/{...}` 中继等令牌模式可用的 POST 路由——账号多租户部署下这些功能全部 403。权限串（Viewer/Editor/Owner）也不含 `analysis.run`。
- 这解释了为什么 10-08 之后（切换账号服务拓扑）视频人物分析再未成功过。

## 修复方向（未实施）

1. **路由映射**：为 `video-people`、`face/{operation}`、`vl/stream`、训练中继补 `RequiredScope` 映射（如 `analysis.run`/`index.write`），并把 `analysis.run` 纳入相应设备授权；补 E2E：账号模式下触发视频人物分析应成功。
2. **关键帧重建**：`SaveKeyframes`/`SaveGeneratedKeyframes` 在删除旧关键帧时，同事务重置 `person_analysis_status`（并记录元数据编辑供同步），使界面如实显示"未分析"；存量数据可按"completed 但无人脸"一次性修复。
3. **照片人脸覆盖**：远程模式提供受授权的照片人脸分析入口（或允许服务端计划器按当前空间补齐），消除 234+388 张的缺口；补齐命令支持多来源空间合并。
4. **模型层进空间**：为远程模式设计制品/模型设置的迁移通道（复用发布器 apply 流程），把本地索引库中的有效制品应用到空间。

## 隐私与安全说明

诊断查询未读取人物姓名/别名、人脸向量内容或凭据；本报告不含任何个人身份数据。
