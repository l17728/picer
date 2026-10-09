# IndexService 一键升级与版本验收（2026-09-30）

更新日期：2026-10-09。当前发布涉及 schema 45，客户端和 IndexService 应配套升级；新的视频内容身份协议不应依赖旧服务正确合并。

## 使用方式

Windows 开发机在仓库根目录运行：

```powershell
.\tools\deploy-indexservice-linux.ps1
```

脚本会发布 Linux x64 自包含程序、生成带部署 ID 与二进制 SHA-256 的清单、上传程序和远端安装脚本，然后自动执行升级及验收。也可以通过 `-Server`、`-User`、`-IdentityFile` 指定目标和 SSH 身份文件。

仅生成升级包、不连接服务器时使用 `-PackageOnly`。

为保证非交互执行，目标机应已配置 SSH 公钥登录，且目标主机密钥已存在于当前 Windows 用户的 `known_hosts`。脚本使用 `BatchMode=yes` 与 `StrictHostKeyChecking=yes`；认证或主机密钥检查不满足时会立即失败，不会尝试口令或关闭主机校验。私钥不应放入仓库。

也支持服务器上执行：将生成的 `picer-index-<deploymentId>.zip` 与 `deploy-indexservice-linux.sh` 放在同一目录（目录中只放一个 `picer-index-*.zip`），以 root 运行 `bash deploy-indexservice-linux.sh`，无需参数。该模式会从包内清单读取 ID 和 SHA，并保留上传文件供审计；安装器自身负责服务停启、回滚和验收。

## 服务端执行与回滚

远端安装器在校验 ZIP 白名单、部署 ID、清单和可执行文件 SHA-256 后，在 `/opt/picer-index.backup-<deploymentId>` 保存当前程序和清单。升级期间停止 `picer-index-health.timer`、健康检查服务和主服务；额外写入服务通过 PowerShell 的 `-AdditionalWriterServices` 或服务器环境变量 `PICER_DEPLOY_WRITER_UNITS` 指定（空格分隔的 `.service` 名称）。这些额外服务只有原来处于运行状态才会恢复。调用方应事先停用其他可自动启动写入者的调度器，不能遗漏独立进程或其它主机的共享盘写入者。

安装器使用 `flock` 防止重复部署，以 `fuser` 检查 DB、WAL、SHM 和 `.access` 不再有进程持有，并在原始文件复制期间持有 POSIX 排他文件锁。关闭状态的 `picer.db`、存在的 WAL/SHM 全量复制至备份的 `database` 目录，生成 `SHA256SUMS`。所需命令包括 `python3`、`fuser`（通常由 `psmisc` 提供）、`flock`、`unzip`、`sha256sum`、`curl`。数据根固定为 `/var/lib/picer-index`，脚本拒绝其它根目录配置；实际服务必须使用该位置。

发现数据库仍打开、已有迁移 pending 标记、备份不完整或部署 ID 已使用时拒绝继续。首次安装不适用本升级脚本，必须存在可回滚的旧程序和数据库。完整备份成功后才原子替换程序，再启动并等待数据库迁移。

安装器轮询 `/health` 与 `/version`。只有健康状态正常且 `/version` 返回本次部署 ID 与期望二进制 SHA-256，升级才会报告成功。若启动、健康检查或版本校验失败，再次停写并验证备份哈希，恢复升级前数据库、WAL/SHM、旧程序和清单；新版本产生而原快照没有的 WAL/SHM 会清除。失败迁移的 pending 标记移入部署备份保存，防止旧程序误恢复新库。证书、服务配置及原始媒体不在升级包内。

恢复数据库失败时不会启动旧二进制访问可能已升级的库，也不会恢复健康计时器；保留备份供人工处理，退出码为 6。正常升级或完整回滚后恢复原先运行的额外写入服务及健康计时器。应用启动本身也会对旧 schema 做关闭备份和暂存迁移，但不能代替部署前的整组回滚快照。请保留备份到客户端与服务验收完成；备份包含私人索引数据，应限制访问权限。脚本不会将应用日志或凭据打印到部署输出。

Windows 离线包使用 `publish-self-contained.ps1`，必须指定两个不同、互不包含的空 staging 目录；脚本拒绝覆盖已有安装。它只构建，不负责停止 Windows GUI/MediaAgent 或部署数据库。Windows 安装替换前仍须停止所有本机写入者，备份相应数据根的 DB/WAL/SHM，验收失败时同时恢复数据与旧程序。

## 版本查询 API

`GET https://<server>:8445/version` 无需 Bearer token，只返回非敏感构建信息：服务名、程序集/信息版本、部署 ID、二进制 SHA-256、源码提交和构建时间、进程启动时间。不返回服务配置、凭据、数据库路径或用户数据。升级脚本和运维检查均以 `deploymentId` + `binarySha256` 确认实际运行的是哪个构建；`/health` 仍用于检查服务可用性。

## 验证

- `GatewayContractTests.Version_ReportsAssemblyAndOptionalDeploymentIdentityWithoutAuthentication` 验证版本接口可读、字段结构正确且无需 API 鉴权。
- `tools/deploy-indexservice-linux.ps1` 先通过 PowerShell AST 解析，再执行 Linux `dotnet publish`、上传、远端升级和版本 SHA 验收。
- 远端升级不会因为 HTTP `/health` 成功就误报新程序已部署；必须匹配清单中的部署 ID 和可执行文件哈希。
