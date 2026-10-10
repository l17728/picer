# Picer AI 问答流式代理 SDD

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
> 后续替代：MediaAgent 已提供授权的本地模型 HTTP 代理和任务执行，正文“目前只读取原图/视频”是早期设计基线。流式问答和取消行为以当前实现及最新验证为准。
<!-- /historical-status-2026-10-10 -->

版本：1.0  
日期：2026-09-24  
状态：核心流式代理已实现；本文中的初始缺口与分阶段计划为历史记录，路由以 2026-10-04 代码核对说明为准。

> 架构校正（2026-10-04，以现行代码为准）：本文早期方案中“Local GUI 直接连接本地模型”的描述已过时。Local/Remote GUI 的 AI 问答现在都通过配对的 IndexService；Local 模式仍使用本机 SQLite/LocalGateway 作为索引事实源，远程 IndexService 只承担 AI 中继（以及用户显式启用时的索引同步）。模型调用由 IndexService 通过 MediaAgent WSS 转发到 Agent 的 loopback endpoint。`AppServices.TryGetAiServiceBackend` 没有 localhost 模型直连回退；未配置 IndexService 时 AI 功能不可用。注意 MediaAgent 当前通过进程环境变量 `PICER_LOCAL_MODEL_ENDPOINT` 选择问答上游，默认 `http://127.0.0.1:8090/api/vl/chat`；IndexService 广播的运行时配置不会覆盖此变量。

## 1. 目标与范围

本设计把 AI 问答纳入 Picer 的客户端/IndexService/本地驻留代理架构，使本地和远程 GUI 使用同一套问答协议。

- 本地 GUI：AI 问答访问配对的 HTTPS IndexService；索引查询仍使用本机索引。
- 远程 GUI：访问 HTTPS IndexService；索引与 AI 均按远程模式的服务契约处理。
- 原图、视频和 AI 请求共用同一个 MediaAgent 长连接，但请求 ID、权限和生命周期彼此隔离。
- 问答必须支持阶段进度、文本增量、媒体结果、取消和错误事件。

本设计不允许远程 GUI 直接访问源机器的 `127.0.0.1`，也不允许 IndexService 代持本地模型 API Key。

## 2. 初始缺口（历史记录）

1. `OpenAiCompatibleChatClient` 当前缓冲完整响应，不支持 token 流。
2. `HttpIndexGateway` 没有 AI 流式接口。
3. `IndexService` 的 AgentConnectionRegistry 目前只处理 `asset.open/asset.response`。
4. `Picer.MediaAgent` 目前只读取原图/视频，没有代理本地模型 HTTP 请求。
5. `AiMediaRetrievalService` 的规划和候选复核需要完整 JSON，不能直接把半截 JSON 暴露给 UI。

## 3. 总体架构

```text
Local GUI ──本地 SQLite 检索
    └──────HTTPS/SSE──────┐
                          v
                    Remote IndexService ──WSS──> 本机 MediaAgent
                                                  └─HTTP──> 配置的 loopback 模型端点

Remote GUI ──HTTPS/SSE────> Remote IndexService ──WSS──> 源机 MediaAgent
                                                       └─HTTP──> 配置的 loopback 模型端点
```

### 3.1 统一应用接口

新增 `IAiQuestionClient`：

```csharp
IAsyncEnumerable<AiQuestionEvent> AskStreamingAsync(
    AiQuestionRequest request,
    CancellationToken cancellationToken);
```

现行实现两种 GUI 模式都由 `AppServices.CreateAiChatClient` 解析 IndexService endpoint 并创建流式客户端；GUI 不直接连接模型 endpoint。此处取代早期方案中的 `LocalAiQuestionClient` 直连设计。

### 3.2 事件协议

IndexService 对 GUI 使用 SSE：

```text
event: stage
data: {"requestId":"...","stage":"理解问题","percent":5}

event: delta
data: {"requestId":"...","text":"找到"}

event: media
data: {"requestId":"...","photoIds":[1,2],"videoIds":[]}

event: done
data: {"requestId":"..."}
```

事件类型：`accepted`、`stage`、`delta`、`media`、`done`、`error`、`cancelled`。

所有事件带 `requestId`。SSE 断线时客户端必须取消原请求，不自动重放产生副作用的模型请求。

### 3.3 MediaAgent WebSocket 协议

IndexService 与 MediaAgent 使用已有 WSS 长连接，新增：

- `ai.open`：请求 ID、模型服务路由、请求体、最大时长、能力声明。
- `ai.chunk`：上游响应的 UTF-8 SSE/NDJSON 字节块。
- `ai.done`：上游完成及 HTTP 状态。
- `ai.error`：错误类型和可展示消息。
- `ai.cancel`：取消指定请求。

请求体不包含 API Key。MediaAgent 只允许连接配置的 loopback 模型端点，并限制请求大小、并发数和超时。

## 4. AI 问答流水线

问答不是单次文本转发，而是四阶段：

1. **规划**：模型输出结构化人物、地点、时间、行为、关键词 JSON。此阶段必须完整缓冲并校验。
2. **检索**：本地索引工具执行人物标签、地理、时间和文本索引检索；阶段状态实时发送给 UI。
3. **复核**：模型只能从候选 ID 中选择，人工确认人物标签是权威事实。
4. **回答**：依据已确认候选生成 Markdown，并以 token 流式输出；随后发送 `media` 和 `done`。

规划和复核的结构化响应只在完整、合法 JSON 后进入下一阶段，不能把半截 JSON 当作答案显示。回答阶段使用 `stream=true`，实现真正的增量文本显示。

## 5. 本地/远程路由

### 本地模式（现行代码）

- `OpenAiCompatibleChatClient` 请求配对 IndexService 的 `/api/ai/v1/chat/stream`；检索侧仍使用本机 SQLite。
- IndexService 经在线 MediaAgent 转发到该 Agent 配置的 loopback 模型 endpoint。IndexService/Agent 未配置时，客户端拒绝直接连本地模型并报告服务不可用。
- GUI 提交所选模型标识与问答请求；模型端点及凭据由服务/Agent 侧配置，问答请求不会让 GUI 指定任意目标主机。
- 仍转换为相同 `AiQuestionEvent`，保证 UI 逻辑一致。

### 远程模式

- `OpenAiCompatibleChatClient` 请求 `IndexService /api/ai/v1/chat/stream`。
- IndexService 校验 Bearer token，选择唯一在线 MediaAgent。
- MediaAgent 使用 HttpClient 请求 `PICER_LOCAL_MODEL_ENDPOINT` 指定的 loopback endpoint；未设置时默认 `http://127.0.0.1:8090/api/vl/chat`。该值来自 MediaAgent 进程环境变量，不是 GUI 或 IndexService 的动态模型选择。
- 响应块沿 WSS 和 SSE 原样转发，IndexService 不解析模型私有 token 格式。

## 6. 取消、超时与恢复

- GUI 点击停止：SSE 请求取消 → IndexService 发送 `ai.cancel` → MediaAgent 取消 HttpClient 请求。
- GUI 断线：IndexService 在 `RequestAborted` 时取消 Agent 请求。
- 规划、检索、复核、回答分别有超时；默认总超时 5 分钟，阶段超时可配置。
- 上游模型不可用时返回结构化 `error`，UI 保留已显示文本和失败阶段。
- 不自动重放模型请求，避免重复计费和重复生成。

## 7. 安全与隐私

- IndexService API 继续使用 HTTPS + Bearer token。
- MediaAgent WSS 使用已有证书 pin 和 token。
- 模型 API Key 只存储在源机器 Credential Manager/DPAPI，不经过 IndexService 和远程 GUI。
- 日志只记录 requestId、阶段、耗时、字节数和错误类型，不记录问题原文、人物姓名、图片路径或模型密钥。
- MediaAgent 只允许 loopback 模型地址，禁止通过请求体改变目标主机。
- 请求体大小、并发数、单请求输出字节数必须有上限。

## 8. UI 行为

- 点击提交立即显示“正在理解问题”，按钮进入运行状态。
- 阶段进度持续更新；收到 `delta` 立即追加 Markdown。
- 收到 `media` 后渐进加载缩略图，不等待完整回答才显示。
- 停止按钮始终可用；重复提交被忽略。
- 远程模式显示“通过 IndexService 使用源机器本地模型”，本地模式显示“本机模型”。

## 9. 测试与验收

### 单元测试

- SSE 多块拼接、跨块 UTF-8、心跳和结束事件。
- WebSocket `ai.open/chunk/done/cancel` 请求多路复用。
- 上游 4xx/5xx、超时、断线、取消传播。
- API Key 不出现在 IndexService 请求和日志中。
- 规划/复核半截 JSON 不会展示为最终答案。

### 集成测试（现行路由）

- Local GUI → 本机索引检索 + 配对 IndexService → MediaAgent → loopback 模型服务 → 流式事件。
- Remote GUI → IndexService → MediaAgent → `127.0.0.1` 模型服务 → Remote GUI。
- 远程机器没有本地模型端口时返回明确错误。
- 同一 requestId 的事件顺序正确，不同请求互不串流。

### E2E 验收

1. 本地输入问题，100ms 内出现阶段状态，随后出现增量文本。
2. 远程输入同一问题，目录、照片、视频结果与本地一致。
3. 点击停止后 2 秒内上游请求终止，UI 不再追加文本。
4. 断开 MediaAgent，UI 显示代理离线，不挂死窗口。
5. 检查日志和抓包，确认没有 API Key、原始路径和问题全文泄露。

## 10. 原始分阶段实施记录（历史）

1. 抽象 `IAiQuestionClient` 和事件模型。
2. 实现本地模型 SSE 客户端和回答阶段流式生成。
3. 扩展 MediaAgent/IndexService WSS AI 协议。
4. 实现远程 SSE 网关与取消传播。
5. 改造 MainViewModel，接入阶段、文本和媒体事件。
6. 完成单元、集成、E2E 和断线回归测试。

评审通过前不修改现有生产路由；新接口采用独立路径，旧的一次性问答接口保留兼容期。
