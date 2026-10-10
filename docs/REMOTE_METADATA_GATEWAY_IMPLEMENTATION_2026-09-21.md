# 远程标签与相册元数据接口（2026-09-21）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

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
