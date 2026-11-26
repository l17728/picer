# Relay 连接身份与控制通道首片（2026-11-26）

## 范围

本阶段先建立本机 IndexService 的独立账号身份及 Relay 持久控制 socket。它为后续本机出站 Connector 和请求转发提供鉴权边界；不把现有账号服务的按请求代理当成本机 Relay 已完成。

## 实现

- 设备种类新增 `index-service`。设备登记可经标准 DPoP enrollment 发起，必须由对应照片空间 owner 在网页批准。无论批准页面的编辑选项如何选择，该身份只获得 `relay.connect`。
- `DeviceAccountClient.ConnectWebSocketAsync` 对 `/api/index/v1/connect` 做精确路径和身份/作用域校验。现有 `agent` 仍只能使用 `source.serve` 的 `/api/agent/v1/*` socket。
- 账号服务的 connector registry 按设备 grant 保存活动 socket；Relay 连接不会启动或占用该空间的云端 IndexService runtime。每 grant 限一个连接，按空间/节点另有限流。
- 控制帧限制为 4 KiB 文本 JSON。初版支持 `index.connector.heartbeat` 和 `index.connector.heartbeat.ack`；注册表记录空间、设备、grant、连接时间和最近心跳。无效控制帧关闭连接。
- 长连接绑定授权撤销观察器。授权被撤销后最多一个观察周期内取消 socket，并移除 online 状态。账号设备页显示本机 IndexService Relay 在线/离线。

## 验证

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：0 警告、0 错误。
- 定向回归 41/41 通过，其中覆盖完整 `AccountAuthorizationTests`、Relay connector 配对/心跳/撤销测试、现有 AI 作用域回归。
- Connector 集成测试确认新身份无法 `GET /health` 读取云端索引，且不能打开驻留 MediaAgent socket；撤销后连接断开并从注册表移除。
- Release IndexService 构建：0 警告、0 错误；`D:\picer\server\Picer.IndexService\bin\Release\net8.0\Picer.IndexService.dll`，SHA-256 `D40582CBC475E06274E122C1267E5DA9FE7C1026941918A2C7C7575E3D4F0CA2`。本阶段尚未部署生产账号服务，因为本机 Connector 客户端仍未实现。

## 未完成边界

- 本机 IndexService 尚未从安全凭据存储加载服务设备身份，也尚无后台常驻出站重连 worker；当前测试由测试客户端主动连接账号服务。
- 连接目前只有心跳，没有请求/响应多路复用、端到端会话密钥协商、加密 payload 转发或 GUI 到指定本机 IndexService 的查询路由。
- 因此 RELAY-02（完整配对工作流）、RELAY-03（远程查询）、RELAY-07（自动重连）、RELAY-17/18（重放与写入幂等）均未完成，RELAY-01 至 RELAY-40 不得整体标为通过。
