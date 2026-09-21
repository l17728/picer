# 远程标签与相册元数据接口（2026-09-21）

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
