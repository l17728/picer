# 文档刷新、持续集成与部署记录

## 源码与验证

产品实现与文档主提交为 `39aba5b1ab189cd2495118ec2489c16d30979fd8`。首个候选 `v1.1.0` 指向 `88a12bfe353449b4459254f3a8fde7b7ef93115f`，曾通过完整 CI；随后再次运行发现视频内容缓存的短间隔改写问题，因此始终保持草稿，未公开。修复后使用 `v1.1.1` 候选，保留原标签作为历史证据。

- 本机重新 restore、Debug 严格构建通过，零警告、零错误。
- [首个候选 GitHub CI](https://github.com/l17728/picer/actions/runs/37928011081)：完整单元/集成 **605/605**，WPF **73/73**，失败和跳过均为零。这是该次完整测试，不再只是之前的分轮去重核验；后续发现的偶发失败另行记录，不能用该次通过掩盖。
- [Release 工作流](https://github.com/l17728/picer/actions/runs/37928114114)：严格构建、两套非 RuntimeComponent 测试、六个组件打包及草稿上传全部成功。
- 第一轮 CI 在 577 项通过后触发 60 秒无测试事件超时；第二轮在测试执行前触发 30 秒宿主连接超时。分别改为 10 分钟挂起诊断和 180 秒连接等待，保留全部测试。原始失败日志保留在本机 `artifacts/ci-run-*`。

补充候选验证：`1f0e376` 修复视频内容缓存，采用卷日志代次与文件 USN；无法取得可信签名时重新读取内容。[v1.1.1 CI](https://github.com/l17728/picer/actions/runs/37932196317) 通过 **610/610 + 73/73**。发布流程重复测试时暴露目录通知测试的异步时序问题；现保留数量、路径和完整性断言，采用最长 3 秒等待通知收敛，严格构建及 6 项定向测试通过。最终候选调整为 `v1.1.2`，待修正提交完整 CI 通过后创建标签。

## 发布资产

最终 `v1.1.2` 的组件与安装方式见[发布安装指南](RELEASE_GUIDE.md)。六个二进制包由 GitHub Actions 生成：Windows GUI（含代理）、独立 Windows MediaAgent、Windows/Linux IndexService、Windows/Linux ModelServer。另补充经固定运行组件检查的 Windows 完整包、版本化 runtime 包与独立校验清单。最终公开状态与资产核验在本轮收尾时记录。

完整包的固定运行组件由 `tools/publish-self-contained.ps1` 校验文件大小、SHA-256、许可证和本机依赖；隔离 GUI 冒烟确认使用包内 `coreclr.dll`，窗口正常响应。固定 runtime 包只有 ffmpeg.exe，不含代理探测所需的 ffprobe.exe；代理需另备同目录的两个工具。外部 Python/CUDA、Qwen/InsightFace 等模型与实际模型端点仍需独立安装配置；这些不因提供 ModelServer 程序包而自动具备。包内安装说明是 CI 构建时快照，Release 顶层 INSTALL.md 修正了 FFprobe 安装说明，以顶层说明为准。

## 部署与数据保护

本机在全部图库写入进程关闭后，排他打开并备份实际 `picer.db`、`remote-mirror.db` 及现有 WAL/SHM，备份路径 `artifacts/deployment-backup-20261009T115432`，包含大小和 SHA-256 清单。先在副本演练，再迁移两个实际数据库，均由 schema 41 升至 45；`quick_check=ok`，外键错误 0。

本机每个图库仍为 8,603 张照片、529 个视频；各自的人工分类、人脸审阅、照片描述和视频摘要数量与迁移前一致。两个库的描述数量本来不同，不把镜像数量与独立本地库混为一谈。

| 位置 | 更新与核验 |
|---|---|
| `D:\PicerData\releases\Picer-win-x64` | 固定 GUI 安装及同包 MediaAgent；新版远程模式窗口响应正常，代理在线 |
| `D:\picer\artifacts\p2-followup-release-20261009` | 更新上次使用的 GUI/代理、Windows IndexService；旧目录另存 backup |
| `D:\picer\artifacts\release` | 更新路径配置默认的 GUI/代理、Windows IndexService 输出 |
| `D:\VL-models\services\Picer.ModelServer` | 完整 Release 依赖及 Python worker；健康检查和程序集哈希通过 |
| 远端 `/opt/picer-index` | Linux IndexService，部署号 `20261009T120246993Z`；健康、部署身份、二进制哈希与 schema 45 核验通过 |

`%LOCALAPPDATA%\Picer` 是指向 `D:\PicerData` 的 junction，不是另一份安装。没有发现另外运行的 Windows 索引服务、计划任务或 GUI 快捷方式。更早的历史发布包和开发输出保留，不覆盖历史证据和回滚包。

ModelServer 备份在 `D:\VL-models\service-backups\modelserver-20261009T120220313`，包括原程序、启动脚本与适用的训练归档；升级不执行训练。Linux 程序与闭库数据备份在 `/opt/picer-index.backup-20261009T120246993Z`，备份完整性已验证。线上升级后照片、视频、人工分类、审阅、照片描述和视频摘要数量均与其自身备份一致。

远端二进制 SHA-256：`b85741badb942233bfab12878e4e1cb7a4f00acc4a52336112060a1a44860795`。该包发布清单来源提交为 `c9dde6091801`；它与最终标签之间只有 CI/发布工具和文档变化，不能把清单来源伪写为标签提交。

部署后发现旧启动环境没有让 GUI 拉起的代理继承 FFmpeg、ASR、VL、TTS 路由。通过已配对 SPKI 校验的连接，把本机既有有效配置恢复到服务的持久配置并广播，保留已有的人脸服务配置。复查代理在线，FFmpeg、ASR、VL、人脸、TTS 能力均 ready，训练代理为 configured；这只是连接和配置状态，不替代真实模型质量验收。此次使用本机既有外部 FFmpeg 目录（ffmpeg.exe 与 ffprobe.exe 齐全）及模型端点，不是仅解压完整包后的全新环境验证。

Linux 备份工具另用临时 SQLite/WAL 合成数据验证了完整复制、恢复、损坏拒绝、独立持锁拒绝及多余 sidecar 清除；未用真实库注入故障。本机安装目录切换遇到新 staging 目录重命名访问拒绝，旧目录立即恢复；随后保留旧目录备份，复制并核验新安装文件。

## 文档与范围

五份主文档重写过时正文；118 份历史报告/经验页补充历史语境及具体替代状态，保留旧测试和部署事实。新增[文档导航](README.md)，并刷新服务部署说明。敏感临时探测脚本、数据库、模型、缓存和本机凭据均未提交。

本轮未重跑付费描述、真实媒体移动/清除、人脸质量、人工 ASR 对照、真人 AEC3 或低显存验收；四项旧 P2 继续暂停。原始照片和视频未被修改或删除。

本机验证明细位于 `artifacts/deploy/`、`artifacts/final-ci-88a12bf/`，它们不随仓库提交。
