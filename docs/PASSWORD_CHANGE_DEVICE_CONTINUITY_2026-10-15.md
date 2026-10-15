# 密码修改后的设备连续性（2026-10-15）

## 变更

用户使用当前密码主动修改登录密码时，更新账号密码哈希，但保留已经单独配对授权的 GUI 和 MediaAgent 设备会话。短期访问令牌可继续使用，设备刷新令牌仍可按原有轮换流程刷新。门户不再提示“所有设备需要重新登录”。

设备仍可在账号门户设备列表中逐台撤销。管理员重置密码仍递增账号 `security_epoch`、要求用户首次登录改密，并使已有设备凭据失效；这保留了账号恢复场景下的安全撤销行为。

## 原因

之前主动改密也递增 `security_epoch`，所有设备 API 会话立即失效，且刷新令牌因 epoch 不匹配无法续期。MediaAgent 不保存账号密码，无法静默重新配对，导致本机媒体读取（包括视频缩略图）中断。这与用户修改自己的密码后继续使用已授权设备的预期不符。

## 验证

`AccountAuthorizationTests.ChangingPasswordPreservesExistingDeviceAccessAndRefresh` 覆盖旧密码失效、新密码生效、已配对设备访问继续有效及刷新令牌轮换。`AccountPasswordAdministrationTests.AdministrativeResetRevokesOldCredentialAndRequiresChange` 保证管理员重置仍撤销旧设备凭据。

部署后还需对生产 MediaAgent 媒体读取和真实服务探针做验收。生产账号此前已执行过一次旧逻辑改密，旧设备会话已经失效；仅部署此修复不会恢复已失效会话，需要对现存 MediaAgent 做一次配对恢复。后续用户主动改密不再中断设备。
