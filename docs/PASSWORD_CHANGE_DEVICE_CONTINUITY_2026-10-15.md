# 密码修改后的设备连续性（2026-10-15）

## 变更

用户使用当前密码主动修改登录密码时，更新账号密码哈希，但保留已经单独配对授权的 GUI 和 MediaAgent 设备会话。短期访问令牌可继续使用，设备刷新令牌仍可按原有轮换流程刷新。门户不再提示“所有设备需要重新登录”。

设备仍可在账号门户设备列表中逐台撤销。管理员重置密码仍递增账号 `security_epoch`、要求用户首次登录改密，并使已有设备凭据失效；这保留了账号恢复场景下的安全撤销行为。

## 原因

之前主动改密也递增 `security_epoch`，所有设备 API 会话立即失效，且刷新令牌因 epoch 不匹配无法续期。MediaAgent 不保存账号密码，无法静默重新配对，导致本机媒体读取（包括视频缩略图）中断。这与用户修改自己的密码后继续使用已授权设备的预期不符。

## 验证

`AccountAuthorizationTests.ChangingPasswordPreservesExistingDeviceAccessAndRefresh` 覆盖旧密码失效、新密码生效、已配对设备访问继续有效及刷新令牌轮换。`AccountPasswordAdministrationTests.AdministrativeResetRevokesOldCredentialAndRequiresChange` 保证管理员重置仍撤销旧设备凭据。

## 生产部署记录

2026-10-15 已部署到生产 IndexService：提交 `34877c678816`，部署号 `20261015T024053934Z`，二进制 SHA-256 `a733e8a831491cad9677e8b0757329c173381322db5edf40de2bf739b04d719b`。原子升级脚本已生成备份 `/opt/picer-index.backup-20261015T024053934Z`；部署后 `/health` 为 `ok`，`/version` 与提交、部署号及二进制哈希一致。

初始 CI `38667943065`、`38671650255` 和 `38672946470` 首次执行均在 `video-thumbnail-fetch` 失败，因为此前密码修改已使本机 MediaAgent 的旧授权会话失效；服务部署本身不会复活旧会话。随后以原设备密钥重新配对，保留原来的五个媒体根目录，并将原照片来源 `sourceId=1` 重新绑定到新 Agent 授权。重配后真实 `Picer.ServiceProbe` 通过账户登录、照片/视频缩略图解码及视频 Range 读取；CI `38672946470` 重跑后构建、真实服务、单元/集成和桌面 E2E 全部通过。索引与原始照片/视频均未修改。后续用户主动改密会保留已授权设备连接。
