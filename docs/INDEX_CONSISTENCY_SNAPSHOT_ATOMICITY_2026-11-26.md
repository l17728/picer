# 索引切换一致性快照竞态保护（2026-11-26）

## 根因

模式切换的远端一致性快照通过多个 HTTP 请求读取：先读取 revision，再按来源分页读取照片和视频，最后读取元数据。采集窗口内如果有远端编辑，返回值可能混合多个时点，但原实现没有在采集结束时复核 revision。

## 修复

`IndexConsistencySnapshotBuilder.CaptureRemoteAsync` 现在在所有分页和元数据读取完成后再次读取服务端变化游标。epoch、媒体 revision、metadata revision 必须与采集开始时完全相同；否则抛出 `index_consistency_snapshot_changed`。现有模式切换编排器捕获该错误后失败关闭，不写入新模式配置、不触发镜像清理。

## 验证

`dotnet test tests/Picer.Tests/Picer.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~IndexConsistencySnapshotTests"`：3/3 通过。新增测试使用真实 `LocalGateway` 执行完整快照采集，并在前后 revision 之间注入变化，确认快照被拒绝。

此修复不涉及数据库 schema、原始文件、索引服务部署或公网 Relay；整个 Relay SDD 仍有独立缺项，见 `LOCAL_INDEX_RELAY_IMPLEMENTATION_2026-11-19.md`。

Release GUI 构建：src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe，0 警告、0 错误；SHA-256：368E3BC46224002DDDAF43D7C40CAD160758D0231757A80A7EA19CE74807A4BB。已从此路径重新启动 GUI。

