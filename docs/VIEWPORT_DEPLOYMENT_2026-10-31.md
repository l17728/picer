# 视口缩略图与配对恢复修复部署

日期：2026-10-31。用户明确要求部署新程序。本次部署测试版 `1.6.5-local-viewport.20261031`，没有创建发布标签或正式 Release，也没有代用户登录或修改设备授权。

## 已部署组件

后续更正：此轮 GUI 的窗口响应检查误把初始化错误弹窗认作主窗口；不能据此认定实际图库启动成功。用户报告后已完成后台正常配对，并部署追加修复，实际图库和分类缓存完成状态均通过复验，最新状态见[现场修复记录](STARTUP_ACCOUNT_RECOVERY_2026-10-31.md)。下列进程信息保留为该次部署历史。

- GUI：安装于 `E:\Picer\versions\1.6.5-local-viewport.20261031`，PID32336，窗口已创建并响应。App/Core 程序集摘要与通过回归的候选一致。桌面“Picer-本地测试”快捷方式已指向新启动脚本。
- 本机 IndexService：安装于 `D:\VL-models\services\Picer.IndexService`，PID44280。健康与版本检查通过，schema94。
- 远程 Linux IndexService：部署 ID `20261031T022153078Z`，健康与版本检查通过；4 个索引库检查完成，schema94 不变。二进制 SHA-256 为 `c502167c6878654f85468bcf032706b77ecc6147d588d84167accc2da95fed71`。
- MediaAgent：本轮无需修改驻留端，保留原进程 PID45256、版本 `1.6.5-local-complete.20261030`，响应正常，未启动重复驻留程序。
- ModelServer：本轮不涉及，原进程保持运行；本机 8091 端口健康检查为 ok。

## 备份与验证

GUI 使用独立目录，旧目录保留；53 个离线运行文件共 786978528 字节，从旧安装复制并逐文件校验 SHA-256 一致。这是复制完整性检查，不等于重新执行所有 RuntimeComponent 测试。

本机 IndexService 停写后备份数据库及存在的 WAL/SHM，共 3 个文件；旧程序一并保存在 `E:\picer-deploy-viewport-20261031\local-backup`。启动前，Windows 服务包已通过隔离目录 schema94 启动检查。

远程使用 20149 字节 BSDIFF 补丁重建 142444619 字节服务程序；本地逐字节校验补丁结果，远程核对完整摘要后才运行仓库的备份、停写、升级、回退安装脚本。关闭写入者后的 5 个数据库备份及旧程序保存在 `/opt/picer-index.backup-20261031T022153078Z`。

GUI 沿用已验证候选：严格构建零警告/零错误，相关核心专项 29/29、桌面专项 10/10、完整普通桌面回归 152/152 通过。本轮没有完成完整核心回归，也没有执行 RuntimeComponent。Windows/Linux 服务部署包是同工作区的 Release 平台构建，做了启动及健康验证，不将这些检查说成重新执行全部测试。

## 待用户操作

新 GUI 已打开，可继续手工测试。真实账户授权、真实图库出图，以及客户端取消是否及时释放远程/驻留端资源，仍需后续登录联调；当前部署完成不等于这些现场验收已经完成。

全部部署脚本、程序摘要、备份清单、健康检查及窗口检查证据位于 `E:\picer-deploy-viewport-20261031`。`launch-gui.ps1` 是新版启动入口；`gui-verified.json`、`local-deployed.json`、`remote-deploy.log` 和 `remote-health-version.log` 记录实际部署结果。
