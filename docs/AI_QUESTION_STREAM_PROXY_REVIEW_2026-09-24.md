# AI 问答流式代理设计评审记录

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

日期：2026-09-24  
评审对象：`docs/AI_QUESTION_STREAM_PROXY_SDD_2026-09-24.md`

## 评审结论

三项专项评审均认为总体架构可行，但必须保持以下边界：

1. IndexService 负责远程鉴权、索引检索编排和 SSE 输出。
2. MediaAgent 只负责访问源机器的 loopback 模型服务，不接受远端任意 endpoint 或 API Key。
3. GUI 本地/远程共用同一事件模型；传输实现分别为本地 HTTP/SSE 和远程 HTTPS/SSE。
4. 结构化规划和候选复核必须完整校验 JSON，最终回答才允许 token 流式输出。
5. 每个请求使用独立 requestId、sequence、有限 Channel，并支持取消、超时、断线清理和背压。
6. 日志不得包含原始问题、人物名、图片路径、向量或密钥。

## 必须纳入开发的验收项

- 本地模型返回 SSE 多块、一次性 JSON、错误响应三种情况。
- MediaAgent 与 IndexService 之间多请求并发不串流。
- GUI 取消能终止 IndexService、MediaAgent 和模型 HTTP 请求。
- Agent 断线时所有未完成请求收到明确错误。
- 未授权 HTTPS/WebSocket、伪造模型地址、伪造 API Key 均被拒绝。
- 远程查询只使用 IndexService 的索引候选，不向模型发送全量人物列表。
- 本地/远程相同问题的阶段、媒体结果和最终答案一致。

## 评审意见处理

- 网络协议评审建议：SSE 必须有 `Cache-Control: no-cache`、`X-Accel-Buffering: no`、心跳和单调 sequence。
- AI 流程评审建议：不要把半截 JSON 暴露到 UI；规划/检索/复核/回答分阶段发送事件。
- 安全与 UX 评审建议：提交后一个 UI 帧内显示处理中，取消和旧请求隔离，API Key 只留在源机器。

结论：SDD 无阻塞性缺陷，可进入分阶段实现；首版不实现断线续传，先完成可靠取消、鉴权、背压和端到端流式验收。
