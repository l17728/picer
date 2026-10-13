# IndexService 跨机部署收口（2026-09-22）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
<!-- /historical-status-2026-10-10 -->

## 实际部署

- 目标：`124.156.193.122:8445`，systemd 单元 `picer-index.service`。
- 使用 Linux x64 self-contained 发布包更新 `/opt/picer-index`。
- 更新前自动保留带时间戳的服务端备份目录。
- 服务重启后状态为 `active`，监听 `https://0.0.0.0:8445`。

## 验收结果

使用客户端当前配置的 Bearer token（不写入日志）并跳过命令行 curl 的证书校验，仅用于服务端点探测：

| 端点 | 结果 |
|---|---|
| `/health` | HTTP 200 |
| `/api/gateway/v1/sources` | HTTP 200，返回 1 个源、8603 张图片 |
| `/api/gateway/v1/tags` | HTTP 200 |
| `/api/gateway/v1/albums` | HTTP 200 |
| `/api/gateway/v1/people` | HTTP 200 |

客户端仍通过 `HttpIndexGateway` 执行证书 SPKI 固定校验；curl 的 `-k` 仅用于部署后的网络/API smoke probe，不改变客户端安全策略。

此前旧服务缺少标签、相册、人物路由的问题已消除。远程 GUI 现在可以在后台镜像同步时读取这些元数据，旧服务兼容回退逻辑仍保留。
