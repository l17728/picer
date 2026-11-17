# 2026-11-12 本地发行候选准备

当前未提交工作区基于 `6cdffb16a477fbba5f9a914975549ed6cdd81294`。本记录仅代表本地候选，不代表正式发布或部署。11 月 12 日追加设备认证来源代理重启恢复；`gui-v4` 和 `gui-v5` 均已被后续源码改动取代，最终 `gui-v6` 已完成发布脚本全部门禁。

- 从现用 `1.6.5-local-evidence.20261031` 安装目录恢复到 git 忽略的 `third_party/runtime`；23 个固定运行文件大小及 SHA-256 全部匹配，12 份许可证齐全。应用根目录的 DirectML/ONNX 原生库仍由发布流程独立校验。
- 本机缺 Visual C++ Build Tools，发布脚本增加可选 `-RedistDirectory` 参数；默认 CI 查找 Build Tools 的行为不变。使用 v1.6.4 备份中的四个微软签名有效的 VC143 文件，逐个校验签名、复制哈希后放入 `artifacts/verified-vc143-20261112`。
- `tools/publish-self-contained.ps1 -Version v1.6.5 -SkipArchive` 完成 GUI、MediaAgent 和 Windows IndexService 自包含发布，验证固定离线资产、原生库与许可证。输出在 `artifacts/release-candidate-20261112/gui-v4` 和 `index-service-v4`，未覆盖现有安装。
- 最终 v6 产品程序集 SHA-256：GUI `aefa4f5634a9aae4bbbd499764f8ccdda5ab269997509b2872f0fe9924f6a7b3`；MediaAgent `909eec9bcf590b95bb54dff43388a5e7c5b2c5e762bea91bca51eec7e336287e`；IndexService `8b46d0c5f5ae411220411331d58cf823396c0d4309a10c737808143a3ce87b8c`。输出目录为 `gui-v6` 与 `index-service-v6`。

完整普通核心回归先前因 D: 工作盘脱机中止，432 项通过、无失败，但无最终 TRX，不能计为全套通过。盘恢复后的 `core-detached-final-20261112.trx` 共 1230 项，1228 通过、2 项在并行运行发布和桌面测试时出现 HTTP 超时；相同测试的 6 个参数化用例在 `core-timeout-isolated-20261112.trx` 中单独重跑均通过。整套解决方案随后严格构建通过，0 警告/错误。完整普通核心仍未取得单次全绿记录，不能据此放行正式版本。此前桌面 168/168 与凭据专项 40/40 见 [回归收尾记录](REGRESSION_COMPLETION_2026-11-11.md)。

现场只读检查发现本机 MediaAgent、GUI、IndexService 未运行，ModelServer 曾健康但当前用户的 Picer 账户配置/代理凭据不存在；未确认远端 8603 张分类任务的最终状态或真实模型质量。GitHub CLI 已安装但未登录。正式提交、CI、草稿发布、离线工作流和生产部署尚未执行。

## 设备认证代理重启恢复

设备认证模式原先在 GUI 启动时直接跳过 MediaAgent 自动启动。来源绑定成功后现在保存该账号、空间、GUI/代理授权设备及明确选择的来源 ID 与绝对路径；下次启动仅在两份受 Windows DPAPI 保护的设备凭据与绑定完全一致，且本机当前索引中的来源 ID/路径未变化、目录全部存在时恢复代理。读取来源目录走现有数据库串行调度。任何校验失败均不启动，避免扩大源目录读取范围。最终 `Picer.App` 严格构建 0 警告/错误，连接配置 E2E 12/12 通过；完整普通核心和桌面回归仍在后台运行。尚未在真实已配对账户上验证重启恢复，不能记为现场验收完成。

最终 v6 GUI 已完整复制到 `E:/Picer/versions/1.6.5-local-agentfix.20261112`，1191 个文件；复制后 `Picer.App.dll` SHA-256 与候选包一致。完整桌面回归 `desktop-agent-final-20261112.trx` 为 168/168 通过，但最后的数据库调度微调发生在该轮测试启动后；最终源码重新构建 E2E 0 警告/错误，连接配置专项 `agent-autostart-final-20261112.trx` 为 12/12 通过。此目录尚未启动，也未替换正在使用的安装。

用户反馈 Windows 任务栏图标缺失后，已另建 GUI v7 候选并启动测试版本，见[任务栏图标修复记录](TASKBAR_ICON_FIX_2026-11-12.md)。v6 的“尚未启动”状态描述已由本记录后续现场操作取代。
