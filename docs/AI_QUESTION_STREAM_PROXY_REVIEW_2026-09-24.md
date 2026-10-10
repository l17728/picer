# AI 问答流式代理设计评审记录

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

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
