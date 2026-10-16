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
- 定向授权测试通过；提交 `e0b3d7ef58a9039cf29cf5e49a38a1415428556e` 的 GitHub CI 全绿。
- 生产 IndexService 已通过原子升级脚本部署，部署 ID 为 `20261016T083009784Z`，二进制 SHA-256 为 `07ff7e7a21d9bbcc938ed13d7f2937a64eba8ecf9381c77ede3bbce8b47f103e`。脚本验证了 `/health`、`/version` 部署身份和二进制哈希，并在切换前创建数据库备份。
- 本机 GUI 当前已启动。原描述任务不会自动续跑；在原目录范围重新点击“开始描述照片和视频”即可继续，已有描述会跳过。设备权限已确认包含 `analysis.run`，不需要重新配对。
