# 统一模型平台收尾报告（2026-09-15）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

## 本次完成

1. 客户端训练端点统一为 `http://127.0.0.1:8090/picer`。该根地址下的
   `/api/face-training/*` 由签名的 Picer ModelServer 提供；旧的 manager
   内存训练路由不再作为 GUI 默认路径。已有本机旧配置会自动迁移。
2. 统一平台将 Picer ModelServer 注册为后端 `8091` 服务，模型标识为
   `picer-face-training`，并启用异常自动重启（最多三次）。平台 `8090`
   继续作为统一入口和 `/picer` 代理端口。
3. 已验证平台状态：VL、ASR、TTS、Picer ModelServer 均为 `ready`；GPU
   状态和服务指标可从 `/api/status` 读取。
4. 已验证签名训练入口：`/picer/health`、算法列表、签名公钥均返回 200。
5. 已完成代码构建、Node manager 静态检查和训练存储回归测试。

## 验证记录

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：0 警告、0 错误。
- `npm run check`：通过。
- `npm run test:training`：通过。
- 统一平台 `/api/status`：四项服务 `ready`，Picer 服务 `phase=ready`，
  `autoRestart=true`。
- 签名服务探针：`/picer/health`、`/picer/api/face-training/algorithms`、
  `/picer/api/face-training/signing-key` 均 HTTP 200。

## 仍需目标环境参与

- 真实人物数据集的独立验证和训练质量门禁；
- 真实房间的 10 分钟全双工插话/AEC3 验收；
- 真实视频上的 Qwen3-ASR 与 Whisper 对照；
- 跨机器 HTTPS 证书、SPKI pin 和远端索引联调。

