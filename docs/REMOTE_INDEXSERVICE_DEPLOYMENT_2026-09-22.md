# IndexService 跨机部署收口（2026-09-22）

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

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
