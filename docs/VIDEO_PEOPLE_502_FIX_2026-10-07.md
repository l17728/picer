# 视频人物分析 HTTP 502 修复（2026-10-07）

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

## 根因与复现

- 实际调用视频 7 时，代理离线先返回 HTTP 503 `media_agent_offline`；启动既有代理后稳定复现 HTTP 502 `video_face_analysis_incomplete`。
- 服务持久化配置仍指向 `http://127.0.0.1:8092/api/face`，该端口没有监听。代理记录 `ConnectionError / SocketException`，关键帧 11 分析失败。
- 现有 ModelServer 在 8091 正常监听；使用实际关键帧调用 `/api/face-inference` 返回 HTTP 200、`insightface-arcface`、1 张人脸。
- 更新端点后暴露第二个问题：代理仅读取 `PICER_LOCAL_MODEL_API_KEY`，未复用本机已有的 ModelServer 凭据，实际请求返回 HTTP 401，最终仍被包装为 502。

## 修复

- 经证书公钥校验的 IndexService 配置 API，将人脸端点持久化更新为 `http://127.0.0.1:8091/api/face-inference`；接口确认 `persisted=true`、`applied=true`，配置版本 `20261007095937038`。代理重新连接时会重新接收该配置。
- MediaAgent 通过源码链接复用既有 `ModelServerApiKey.Load`。显式代理凭据优先；仅当目标是 HTTP(S) 回环地址且未显式配置时，才读取本机 ModelServer 凭据。远程或相对地址不读取、不附加自动发现的本机凭据。
- 构建并启动修复版代理。未修改原始视频、关键帧文件或模型空间规则；仍拒绝把分析失败当作零人脸成功。

## 验证

- `dotnet restore picer.sln` 成功。
- `dotnet build picer.sln -c Debug --no-restore --warnaserror` 成功，0 警告、0 错误。
- 新增凭据回归测试 8 项全部通过：IPv4/IPv6/localhost、远程及相对地址、显式配置优先、缺失凭据。
- 视频 7 完整远程请求返回 HTTP 200、`modelVersion=insightface-arcface`、`faceCount=1`；代理记录 `frames=1; failedFrames=0; faces=1`。
- 视频 8（此前失败关键帧 12）返回 HTTP 200、`faceCount=2`；代理记录 `frames=2; failedFrames=0; faces=2`。
- 非 RuntimeComponent E2E 全套：59/59 通过。
- 首次单元全量回归发现共享 Gateway fixture 下发 `agent.config` 被模拟代理误当作任务，造成 HTTP 超时。测试辅助函数改为跳过配置重放、拼接完整 WebSocket 消息，并为等待任务设置 15 秒期限；真实服务逻辑不变。修复后非 RuntimeComponent 单元全套 363/363 通过，耗时约 4 分 55 秒；报告：`artifacts/video-502-fix/video-502-fix.trx`。
