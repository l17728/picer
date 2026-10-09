# 远程标签与相册元数据接口（2026-09-21）

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

## 实现

- `IIndexGateway` 新增只读 `ListTagsAsync`、`ListAlbumsAsync`。
- Local、HTTPS、Named Pipe 三种网关均实现同一协议。
- IndexService 增加 `/api/gateway/v1/tags` 与 `/api/gateway/v1/albums`。
- 远程只读镜像保留标签、相册的服务端 ID，启动后增量写入镜像数据库。
- 原始主界面继续使用本地/远程相同的 `SemanticViewModel`，不再为远程单独维护 UI。

## 验证

- 解决 StubGateway 因接口扩展产生的编译缺口。
- `dotnet build picer.sln -c Debug --no-restore --warnaserror` 通过。
- GatewayContractTests：47/47 通过。
- ServiceBrowseWindowTests：1/1 通过。
