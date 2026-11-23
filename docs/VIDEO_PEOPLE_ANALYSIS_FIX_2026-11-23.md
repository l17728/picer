# 视频人物分析与关键帧重建缺陷修复报告（2026-11-23）

对应[诊断报告](FACE_VIDEO_DATA_LOSS_DIAGNOSIS_2026-11-23.md)中的发现三、发现四，按其推荐顺序实施前两项；发现一、二（照片人脸覆盖、模型层迁移）仍待产品方案。

## 修复一（发现四）：账号服务路由映射缺失导致分析类功能 403

- 现象：账号模式下 `POST /api/service/v1/video-people/{id}` 一律 403（`SpaceRoutePermissions.RequiredScope` 未映射，POST 兜底返回 null），"分析视频人物"自切换账号拓扑以来从未可用。实测复现（20:53 日志：`远程视频人物分析失败；video=7: video_analysis_failed (HTTP 403)`）。
- 修复（`server/Picer.IndexService/Identity/AccountServiceHost.cs`）：
  - `video-people` POST 补入 `analysis.run` 映射；`video-audio` GET 补入 `media.read`；
  - 补齐无 ID 的 AI 路由：`asr`、`tts`、`tts/stream`、`vl`、`vl/stream`、`face/{operation}`、训练中继 `training/api/face-training/{...}`（GET/POST）→ `analysis.run`；
  - 服务管理类路由（`config`、`tasks` 等）保持不映射（账号模式下仍 403），避免扩大管理面。
- 授权语义：`analysis.run` 本就是配对时的可选授权（`ApproveEnrollment(allowAnalysis)`；Web 审批页已有"允许分析"复选框）。**已部署设备需重新配对并勾选"允许分析"才能获得该权限**——这是设计内的授权流程，不是回归。
- 测试：新增 `SpaceRoutePermissionTests`（21 项映射表，含管理路由保持关闭的负例）；`AccountMediaRoutingTests` 新增 `VideoPeopleAnalysisPassesScopeOnlyWithAnalysisGrant`：无 `analysis.run` 的设备得到 403，有该权限的设备通过作用域检查（未知视频 404，非 403）。

## 修复二（发现三）：关键帧重建级联删除人脸但不重置分析状态

- 现象：关键帧重新生成（实测库中 10-20/10-27 全量重建）时，`video_detected_faces.keyframe_id` 外键 `ON DELETE CASCADE` 把人脸删光，`videos.person_analysis_status` 仍是 `completed`，界面显示"已分析"但人脸为空。
- 修复：
  - `VideoRepository.SaveKeyframes`（`src/Picer.Core/Video/VideoIndexService.cs`）与 `IndexServiceHost.SaveGeneratedKeyframes`：在同一事务内删除旧关键帧后，将该内容全部引用的 `person_analysis_status`、`person_analyzed_utc` 置空——状态与人脸数据同生共死，界面如实显示"未分析"，可重新发起分析。
  - 按内容（`content_id`）而非单个引用重置：关键帧与人脸本就是内容级数据，级联删除影响所有引用，重置范围保持一致。
- 存量数据：不做一次性数据修复。修复一部署并重新配对后，对受影响视频重新执行"分析视频人物"即可重建人脸与状态。
- 测试：`VideoIndexTests.RegeneratingKeyframesResetsPersonAnalysisStatus`、`VideoKeyframeConcurrencyTests.GeneratedKeyframeRegenerationResetsPersonAnalysisStatus`。

## 验证

- 严格构建 `--warnaserror`：0 警告 0 错误。
- 定向测试 25/25；视频/账号相关回归 94/94；完整 E2E 171/171（8.4 分钟）。
- 完整单元套件结果见提交后追加记录。

## 部署提示

1. 远端 IndexService（47-103-99-229）需用本提交更新后重启；
2. GUI 设备需重新配对并在 Web 审批页勾选"允许分析"；
3. 上述两步完成后，在视频页对 DXXT7429 等视频重新执行"分析视频人物"即可恢复人脸数据。
