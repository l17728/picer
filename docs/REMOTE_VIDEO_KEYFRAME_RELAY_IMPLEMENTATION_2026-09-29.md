# 远程视频关键帧 Relay 实现记录

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

## 行为

远程 GUI 点击“生成/复用关键帧”时，客户端通过 IndexService 提交视频 ID。IndexService 根据远程索引中的视频路径调用在线 Picer MediaAgent 的 `ffmpeg.keyframes` 能力；MediaAgent 在原图所在机器执行 FFmpeg。

服务端收到关键帧结果后先写入自己的 `video_keyframes` 表并返回带服务端 ID 的关键帧清单。GUI 将清单写入本地远程镜像，并触发增量同步。关键帧图片在显示时通过 IndexService 的关键帧网关和驻留代理读取，原视频不复制到服务端。

## 可靠性

- 远程命令运行时重新解析 IndexService 端点，避免视图模型创建早于配置加载导致“FFmpeg 未就绪”。
- 远程镜像读取关键帧时不再检查客户端本地文件是否存在。
- 本地索引写入后立即排队一次合并的远程增量同步；同步失败保留本地提交，由后台复制循环重试。
- 服务端关键帧入库与响应在同一个请求中完成，避免“客户端拿到结果但远程索引没有记录”。

## 验证

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：通过。
- `GatewayContractTests`：56/56 通过。
- `VideoIndexTests`：10/10 通过。
