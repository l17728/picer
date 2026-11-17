# 最新完整测试版部署与服务器清理

日期：2026-10-30。用户明确授权部署配套程序并启动 GUI 手工测试；随后授权清理服务器作战平台、家庭医生及闲置临时目录。本次没有创建发布标签或正式 Release。

## 部署结果

版本为 `1.6.5-local-complete.20261030`，schema94。本机 GUI/MediaAgent 使用 `E:/picer-remaining-20261030/reviewed/`，产品程序集 SHA-256 与完整普通核心 1202/1202、桌面 147/147 回归的最终候选一致。本机 IndexService 和 ModelServer、远程 Linux IndexService 均从同一工作区源码生成相同版本的部署包；平台包做启动、版本和健康检查，不将这些 smoke 说成再次运行全部套件。

- GUI：PID37032，使用 `D:/PicerData/app-data` 与 `D:/PicerData/bootstrap`；UI Automation 确认响应正常、目录树 15 节点、当前可见照片 20 项。可见项数不是全库数量。
- MediaAgent：PID45256，保留原来源配置及设备凭据，新路径为上述 reviewed 目录。
- 本机 IndexService：PID29296，安装目录 `D:/VL-models/services/Picer.IndexService`；health 为 ok/schema94，数据库 `quick_check` 为 ok。
- 本机 ModelServer：PID52908，安装目录 `D:/VL-models/services/Picer.ModelServer`；health 为 ok，部署后程序集摘要与部署包一致。
- 远程 IndexService：部署 ID `20261030T142234331Z`，二进制 SHA-256 `574071b4ac74fa2b834a249e4a6a1a4282dc5041fbcf08a7a101ecaf00a48b18`。4 个索引库已预迁移为 schema94；正式服务地址的 health/version 再次检查通过。

桌面“Picer-本地测试”快捷方式指向 `E:/picer-deploy-complete-20261030/launch-gui.ps1`。启动最新 GUI 不会自行开始新的模型分析任务。

## 备份与部署过程

本机先关闭 GUI/Agent/IndexService 写入者，再整组复制数据库、WAL、SHM；17 个备份文件共 717725368 字节。数据库与旧服务程序备份位于 `E:/picer-deploy-complete-20261030/local-backup/`，清单为 `database-backup-manifest.json`。模型服务原程序与配置保存在 `D:/VL-models/service-backups/modelserver-20261030T142555271/`。

远程 SFTP 上传较慢，改用本地保留旧二进制生成 BSDIFF 差分（616509 字节）。本地验证补丁重建结果逐字节相同；远程先核对旧二进制摘要，再重建并核对完整新二进制摘要，之后仍执行仓库现有停写、备份、迁移及回退脚本。差分仅用于部署传输，不改变产品同步协议。

远程前两次迁移预检因空间不足安全退出，旧服务恢复，没有绕过“数据库升级余量＋2 GiB”保护。旧备份先压缩并用 tar 内容比较验证后才移除展开目录，归档与摘要仍保留。随后按用户授权清理作战平台，第三次部署成功。最终关闭写入者后的 5 个数据库备份位于 `/opt/picer-index.backup-20261030T142234331Z/database/`，旧程序一并保留。

## 分类授权待用户完成

现场分类读取明确返回授权不足。只读检查当前设备凭据确认：存在有效会话及 `index.read`，缺少 `taxonomy.read`。新版分类读取路由同时要求这两项；旧授权不自动扩权。GUI 的分类错误状态正确显示“需要更新设备授权”，不能记为分类树加载成功。

已向用户提供“更新设备授权 → 忘记连接并重新配对 → 浏览器登录原账户/原空间 → 下载并导入认证文件”的步骤。新配对查看权限包含 `taxonomy.read`；修改、媒体、AI 权限需用户在网页按使用需求明确选择。等待用户导入后重启并复验，未修改服务数据库绕过授权，也未代替用户输入账户密码。

## 服务器清理

- 已确认 `/fighting` 为作战平台，清理它及 `/var/lib/combat-retired`，直接释放 1254793216 字节；无平台运行进程或嵌套挂载。
- 家庭医生前后端容器停止并移除；停止工作目录属于该项目的旧开发进程后，清理 `/homedoctor` 与 `/root/homedoctor-backups`，直接释放 423915520 字节。交互 shell 未强行结束。
- `/tmp`、`/var/tmp` 清理前扫描进程 cwd/exe/fd/maps/命令路径、挂载和 loop backing 文件；保留活跃资源、IPC、服务私有临时树及一小时内的新文件。最终清理 277 项、错误 0，直接释放 19035791360 字节，保留 30 项。没有删除临时根目录或穿透符号链接。
- 最后 `df -h /` 显示约 29 GiB 可用；运行任务也会改变空间，不能把所有前后差额都计为本次清理。Picer 远程服务仍 active，正确公共主机名的 health/version 均通过。

证据与脚本统一位于 `E:/picer-deploy-complete-20261030/`：`remote-final-deploy.log`、`local-deployed.json`、`model-deploy.log`、`installed-gui-verified.json`、`device-scope-check.json`、`combat-cleanup.json`、`homedoctor-cleanup-final.json`、`temp-cleanup-result.json`。真实模型质量、长期使用和用户手工验收尚未代做。
