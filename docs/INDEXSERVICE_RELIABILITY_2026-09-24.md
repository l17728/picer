# IndexService 可靠性部署与验收

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
