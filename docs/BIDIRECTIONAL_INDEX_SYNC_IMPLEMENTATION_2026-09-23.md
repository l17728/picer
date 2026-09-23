# 双向索引增量同步实现（2026-09-23）

## 已实现

- `IndexSyncClient.PushIncrementalAsync` 为本地媒体和元数据计算逻辑指纹，并将成功同步的指纹写入数据根目录检查点。
- 图片、视频、人物、标签、相册、地点、场景、视频分析和人脸工作流均复用现有幂等 `/api/sync/v1/*` upsert 接口；指纹不变时不产生网络写入。
- 远程 GUI 启动时先从 IndexService 拉取镜像，再发布本地镜像变更，避免空镜像覆盖服务端；之后每 30 秒执行“发布→拉取”。
- 本地 GUI 默认保持离线；设置 `PICER_INDEX_SYNC_IP` 与 `PICER_INDEX_SERVICE_TOKEN` 后，会在后台使用本地数据库主动发布到远程 IndexService。
- 所有失败均保留检查点，下一轮自动重试；临时文件原子替换，进程崩溃不会写入半个检查点。

## 一致性边界

当前 IndexService 仍是单租户、服务端最终一致的幂等 upsert。远程与本地同时修改同一实体时，以最后一次成功同步到服务端的完整实体状态为准；下一阶段可在此基础上增加实体版本和冲突工作台。

## 验证

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：通过，0 警告、0 错误。
- `GatewayContractTests`：50/50 通过。
