# 远程视频人物标注显示修复（2026-09-29）

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

## 根因

1. 远程 GUI 的 `AppServices` 在 service 模式下没有及时初始化并拉取视频人物时间线镜像，服务端新增的人物记录留在 IndexService，GUI 本地镜像仍是旧快照。
2. `video.people` 曾把视频文件路径直接交给图片人脸端点 `/api/face`，导致 `invalid_image`，因此 8366 无法产生人物记录。
3. 旧远程数据库不一定包含 `UNIQUE(keyframe_id, ordinal)`，使用带冲突目标的 SQLite upsert 会产生 SQL logic error。

## 修复

- IndexService 将视频的关键帧 ID、时间戳和源机路径随任务发送给 MediaAgent。
- MediaAgent 按关键帧调用图片人脸端点，聚合检测结果并回传原关键帧 ID；视频本身不再作为图片发送。
- IndexService 写入视频人物时改为“先更新、无记录再插入”，兼容旧镜像库并保留人工确认字段。
- service 模式启动时独立执行远程镜像 bootstrap，远程模式始终启动后台拉取，不再依赖可选的发布客户端状态。
- 点击“分析视频人物”后，客户端现在立即拉取远程元数据并重新加载当前视频，不等待下一轮后台同步。
- 远程时间线按镜像的 `video_detected_faces` 与缓存关键帧绑定；显示日志包含 video、关键帧、人脸数量。

## 实机验收

远程 IndexService 数据库中的人物检测记录：

| 视频 | 关键帧 | 人脸记录 |
|---|---:|---:|
| APUA8133.MP4 | 2 | 4 |
| BXZR.mp4 | 2 | 1 |
| DRGR8366.mp4 | 2 | 6 |

8366 通过真实链路 `GUI/HTTP → IndexService → MediaAgent → 127.0.0.1:8092` 重新分析成功；MediaAgent 日志记录 `frames=2; failedFrames=0; faces=6`。人脸契约测试 5/5 通过，App、MediaAgent、IndexService 均以 `--warnaserror` 构建通过。
