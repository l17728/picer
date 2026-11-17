# 描述修复本地测试版构建、配套更新与启动

日期：2026-10-30。用户授权本地 build、更新涉及的其他程序并启动 GUI 手工测试；没有创建标签或发布正式 Release。

## 实际交付范围

版本：`1.6.5-local-description.20261030`。GUI 路径：`E:\picer-manual-20261030\artifacts\bin\Picer.App\debug\Picer.App.exe`。沿用 `D:\PicerData\app-data` 与原账号、来源授权；bootstrap 显式使用 `D:\PicerData\bootstrap`。

工作区的分类 V2 尚未完成验收，因此本次从 HEAD 归档生成独立候选，叠加已验证的描述取消/收尾/首次异常定位、时间版本比较、关键帧限量准备及既有问答修复，保持 schema92。没有把分类 V2/schema93、尚未实现的记录级差分同步加入这次手工测试版。原工作区开发内容保留。候选完整源码、基线提交与 44 项叠加文件摘要保存在 `E:\picer-manual-20261030\source`、`source-manifest.json` 和 `prepare-source.py`。

## 验证

- 独立候选完整解决方案 Debug `--warnaserror` 构建：0 警告、0 错误，`build.log`。
- 候选核心专项：51/51，通过，`core-tests.log` 与 `core-results`。
- 候选 GUI 相关专项：25/25，通过，包含目录描述与 AI 问答刷新，`e2e-tests.log` 与 `e2e-results`。
- Windows IndexService 自包含包隔离启动、HTTPS health 和 schema92 检查通过。
- 尚未进行全量回归、真实模型长时间压力测试或本轮用户手工验收；专项通过不替代这些验收。

## 配套更新和回退

MediaAgent 已从新候选目录启动，保留旧进程必要的 PICER 环境配置，未输出配置值或凭据。原安装版二进制保留。新 Agent PID 41232，服务更新后再次连接成功。

本机 IndexService 原安装采用自带运行时方式，首次用开发输出替换后发现运行时布局不匹配；已恢复原版，再生成完整 Windows 自包含包，经隔离 smoke 后替换。最终 PID 11632，版本 `1.6.5-local-description.20261030`，health 返回 ok/schema92。完整原服务目录、原托管入口和关闭写入者后的数据库/WAL/SHM 备份位于 `E:\picer-manual-20261030\local-backup`。首次格式问题没有执行数据库迁移。

远程 IndexService 配套更新成功，部署 ID `20261030T025405102Z`，可执行文件 SHA-256 `ec75e4d775ef01a1b36648fd96f75c0b115e008009d30e7eb38df400284ea528`。原二进制和 5 个数据库备份保留在 `/opt/picer-index.backup-20261030T025405102Z`；4 个索引库检查均为 schema92。部署脚本完成摘要、健康和版本检查，日志 `remote-deploy.log`。Linux 包清单的 sourceCommit 为 unknown（独立归档不含 .git）；源码归属以本地 source-manifest 为准，不伪称为正式发布提交。

ModelServer 没有本轮协议修改，保留正在使用的版本与模型进程。运行资源沿用已验证的 `E:\PicerRuntime\win-x64-v1.6.4`，以目录联接提供给候选 GUI/Agent，未重新下载模型。

## GUI 启动验收

11:18:08（北京时间）启动 GUI，PID 37684；UI Automation 确认窗口响应、15 个目录节点及 20 个可见照片项。20 是可见列表项，不是图库总数。证据：`started-gui.json`、`installed-gui-verified.json`、`gui-verify.log`。桌面“Picer-本地测试”快捷方式已更新，启动脚本为 `E:\picer-manual-20261030\launch-gui.ps1`。

GUI 保持运行供用户手工测试。可验证目录描述、已有结果跳过、取消后重新开始及失败重试；新增日志现在已在候选 GUI/Agent/服务生效。批量差分同步和分类 V2 继续开发，不能把本次启动视作全部功能已完成。
