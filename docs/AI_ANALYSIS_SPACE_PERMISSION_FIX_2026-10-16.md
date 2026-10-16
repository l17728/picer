# 照片空间 AI 描述权限故障修复（2026-10-16）

## 现象与根因

目录描述在处理第一张照片时收到 HTTP 403，连续失败保护随即暂停任务，并保留尚未处理项目。设备凭据包含 `analysis.run` 和 `answer.run`，因此无需重新配对或重新授权。

照片空间服务端的权限路由表没有为 `POST /api/ai/v1/chat/stream` 配置所需权限。账户空间代理将该请求视为未授权路由并返回 403，导致 GUI 将其记录为不可重试的服务失败。

此前还发现 GUI 分析器错误地要求传统 API key，与 DPoP 设备认证冲突。分析器现可由认证 HTTP 客户端提供凭据，并保留其 DPoP 头；这项客户端修复已通过回归测试并用于本机修复版 GUI。

## 修复

服务端将 AI 流式接口明确映射到 `analysis.run`。设备未获此权限时仍返回拒绝；获得授权的设备可以调用。批处理遇到认证拒绝仍会暂停并保留未处理项，避免无意义重试。

## 验证与部署

- `AiStreamRequiresExplicitAnalysisGrant` 验证 AI 流式接口只接受 POST 且要求 `analysis.run`。
- `CompatibleAnalyzer_UsesDeviceAuthenticationFromHttpClient` 验证 DPoP 凭据不会被空 API key 覆盖。
- 生产修复需要部署更新后的 IndexService；部署后应验证 `/version` 的部署身份，并使用现有获准设备重新运行目录描述。已完成的描述会跳过，待处理项目继续执行。
