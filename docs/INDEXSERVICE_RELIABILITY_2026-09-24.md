# IndexService 可靠性部署与验收

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

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
