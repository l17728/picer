# 账号发布器模式切换完整性门禁（2026-11-26）

## 问题

切换事务原有一致性门禁只覆盖普通同步网关快照。账号照片空间使用来源范围发布器时，`_localSpacePublisher != null`，此前跳过了本地/远端快照比较；因此虽然发布调用返回成功，切换仍缺少“整库范围完整、发布检查点已在服务端确认、当前来源根目录可读”的证据。

## 实施

- 增加账号发布器专用的切换验证：本地所有来源必须都在发布范围内；范围关闭但本地存在的照片、视频、人物/人脸、审核/候选/训练信息或精确位置数据会阻止切换。
- 要求本地发布器媒体与元数据 revision 已上传、scope/protocol 检查点匹配、待处理 outbox/导出/身份 adoption/元数据/描述/目录同步和待审核冲突均清空。
- 从账号服务核对 publisher ID、epoch、sequence、设备身份和 metadata protocol 与本地已确认检查点相同。
- 通过账号空间网关逐项验证本地来源路径在远端存在，再执行既有目录/搜索/缩略图只读探针。任何失败仍由切换事务保留原模式。
- 远端来源路径按该 publisher 的 namespace 进行限定，避免仅比较 GUI 本地路径和服务端逻辑路径造成误报。

## 验证

- `AccountSourcePurgeUsesStableIdentityAndRecoversWithoutTouchingAnotherSourceOrOriginal`：4/4 参数场景通过，3 分 40 秒。
- 切换状态机、配置、一致性快照和镜像清理定向回归：15/15 通过。
- `dotnet build src/Picer.App/Picer.App.csproj -c Debug --no-restore --warnaserror`：0 警告、0 错误。
- Release GUI：`dotnet build src/Picer.App/Picer.App.csproj -c Release --no-restore --warnaserror -p:OutDir=artifacts/index-mode-switch-account-verification/win-x64/`，0 警告、0 错误。隔离产物 GUI：`src/Picer.App/artifacts/index-mode-switch-account-verification/win-x64/Picer.App.exe`；`Picer.App.dll` SHA-256：`A348E7521764A9EF690AFD7001D5A6903DB51C0E62804008270AF54589A6F3C3`；没有覆盖仍运行中的正式 GUI。

## 边界

此阶段只加强现有服务索引切换事务。它不实现 SDD 尚缺的 IndexService 主动 Relay 长连接、密文多路复用、Relay 零落盘证明、媒体缓存加密或 Web 管理控制台；这些仍需按 `LOCAL_INDEX_RELAY_SDD_2026-11-19.md` 和 RELAY-01 至 RELAY-40 继续开发与逐项验收。本报告不宣称完整 Relay 目标已完成。
