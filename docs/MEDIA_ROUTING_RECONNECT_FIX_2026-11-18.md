# 媒体路由重连修复（2026-11-18）

## 根因

隔离空间中的索引记录保存了旧的驻留设备标识。驻留程序重连后使用新的授权会话注册，索引服务按旧标识查找不到会话，缩略图和原图请求分别等待两次 2.5 秒后返回 `media_agent_offline`。

## 修复

`AgentConnectionRegistry` 在严格隔离路由下增加安全回退：显式标识未找到时，仅当当前隔离空间恰好有一个具备 `media.binary.v1` 能力的驻留会话，才绑定该会话；存在多个会话时仍拒绝猜测，保持隔离和授权边界。

## 验收

- Release 构建：`dotnet publish server/Picer.IndexService/Picer.IndexService.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeRuntimeAssets=false`
- 本地提交：`c6c6292 fix: recover media routing after agent reconnect`
- 新服务器二进制 SHA-256：`f533165d559c423f943b76a9d634b28dfcdb8cb440ded8fb0b739aa16a6b6ef9`
- 部署路径：`/opt/picer-index/Picer.IndexService`
- 服务：`picer-index.service`，状态 `active`
- 端到端未缓存缩略图 `imageId=8234`：驻留程序读取并生成 9424 字节 JPEG，服务端缓存后返回 HTTP 200。
