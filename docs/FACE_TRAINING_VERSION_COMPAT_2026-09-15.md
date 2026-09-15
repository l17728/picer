# 人脸训练制品版本字段兼容修复（2026-09-15）

## 问题

人脸精度验证窗口提交训练后，客户端提示“训练制品基础模型版本不匹配”。ModelServer 当前制品协议返回 `modelVersion`，而客户端旧协议只读取 `baseModelVersion`，导致同一版本被解析为空并误报不匹配。

## 修复

- 客户端同时读取 `baseModelVersion`（新协议）和 `modelVersion`（v1 协议）。
- 仍要求解析后的版本与本次数据集版本逐字精确相等，不放宽跨模型版本混用保护。
- SHA-256 校验统一为服务端约定：先将 `sha256`、`signature`、`signatureAlgorithm`、`signingKeyId` 置空，再计算哈希。
- 在应用日志记录期望版本、制品版本和制品 ID，便于定位真实版本不一致。
- 增加两个字段协议兼容的单元回归用例。

## 验证

- `dotnet build picer.sln -c Debug --no-restore --warnaserror` 通过。
- `FaceTrainingEndpointTests`：5/5 通过。
- 已同步并重启本地 ModelServer（8091），启动最新 Picer GUI。
