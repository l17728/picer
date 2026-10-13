# IndexService 可靠性部署与验收

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
<!-- /historical-status-2026-10-10 -->

## 已实现

- 客户端 AI 请求对 TLS、超时和 502/503/504 自动重试。
- IndexService 在 MediaAgent 短暂重连时等待代理恢复，避免立即返回 503。
- 客户端、IndexService、MediaAgent 使用 `X-Picer-Request-Id` 关联请求日志。
- 日志记录阶段、耗时、状态码、重试次数和异常类型，不记录问题原文、Token、路径、图片或向量。

## 远程服务部署模板

`deploy/systemd/picer-index.service` 提供进程异常自动重启和退避；
`picer-index-health.timer` 每 30 秒检查 `/health`，失败时重启服务。
`tools/install-indexservice-systemd.sh` 用于 Linux 主机安装这些单元。

安装后应验收：

1. `systemctl is-active picer-index.service` 返回 `active`；
2. `systemctl is-active picer-index-health.timer` 返回 `active`；
3. `/health` 连续 10 次成功；
4. MediaAgent 断开后能自动重连；
5. AI 请求日志中的 requestId 在三端一致。

本地仓库只能生成和验证部署模板，不能替代远程主机上的 systemd 安装。
