# 双向索引增量同步设计

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
> 后续替代：IndexService 已提供独立 GUI/Agent、授权同步、分析、修复和索引清除接口；“仅只读网关”“远程人工修改不回传”“等待批准”的表述均为本阶段背景。原始媒体仍只读，索引清除不等于删除原文件，远端能力仍受配置及授权约束。
<!-- /historical-status-2026-10-10 -->

## 当前状态

IndexService 当前是只读网关。远程客户端将操作写入本地 `remote-mirror.db`，后台轮询只从服务端拉取元数据；远程人工分类不会反向写回服务端，也不会自动进入 local 数据库。

## 目标

在单租户前提下实现带游标的双向增量同步：

1. 每个写操作生成不可变 `change_id`、`entity_type`、`entity_id`、`operation`、`payload`、`base_version` 和 `created_utc`，写入本地 outbox。
2. 客户端先提交 outbox，服务端按 `change_id` 幂等接收并分配全局 `server_version`。
3. 客户端按 `server_version` 游标拉取其他客户端变更，事务性应用到本地数据库。
4. 同一实体冲突采用版本检查；人工确认/不分类优先级高于自动建议，冲突保留审计记录，不静默覆盖。
5. 同步成功后保留 outbox 状态和服务端确认号，失败重试使用指数退避；离线时所有本地操作仍可用。

## 接口草案

- `POST /api/sync/v2/changes`：批量提交变更，返回每项 accepted/conflict/serverVersion。
- `GET /api/sync/v2/changes?after=<cursor>&limit=<n>`：按服务端游标增量拉取。
- `GET /api/sync/v2/cursor`：读取当前服务端版本。
- `POST /api/sync/v2/ack`：确认客户端已应用游标。

原始文件和人脸裁剪图不写入索引同步流；索引只同步路径、框、向量版本、标签和分类状态。原图/裁剪图仍通过 MediaAgent 中转并缓存。

## 验收重点

- local 操作 → 服务端 → remote 可见；remote 操作 → 服务端 → local 可见。
- 重复提交不产生重复状态；断网恢复不丢操作。
- 同时移动同一人脸时，冲突可解释、可审计。
- 增量同步不重建目录树、不修改原始文件。
