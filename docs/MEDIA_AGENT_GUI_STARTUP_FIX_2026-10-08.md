# GUI 启动后驻留代理退出排查（2026-10-08）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

## 现场与根因

- GUI 09:19 启动后记录“解析到本机授权根目录”，随后报告代理启动失败；系统中 GUI 仍运行，MediaAgent 已不存在。代理日志在 GUI 启动前停止更新，没有新的视频任务或推理异常。
- GUI 目录残留 `src/Picer.App/bin/Debug/net8.0-windows/Picer.MediaAgent.exe`（2026-09-24），缺少相邻的 `Picer.MediaAgent.dll`。直接运行复现退出码 `-2147450726`，运行时明确提示目标 DLL 不存在。
- `MediaAgentController.Start` 先停止现有代理，再按 `File.Exists` 选择程序。孤立 EXE 优先于完整的开发目录代理，导致正常驻留代理被停止，替代进程立即退出。
- 之前的 502 修复通过了直接启动代理的验收，但未覆盖 GUI 的实际启动选择路径。本次故障属于启动部署完整性问题，不是视频推理导致代理崩溃。

## 修复

- 按现有发布布局，候选代理必须同时包含 EXE、DLL、runtimeconfig.json 和 deps.json；跳过不完整的残留文件。
- 在确认可用候选后才停止既有代理；无完整候选时保留旧代理。
- 启动后快速退出记录退出码，GUI 不再将所有启动失败都误报为“未找到可执行文件”。
- 回归测试覆盖 GUI 同目录的孤立 EXE、完整开发目录候选及缺少配套 DLL 时跳过该候选的情形。

## 验证

- 全解决方案 Debug 构建（`--no-restore --warnaserror`）通过，0 警告、0 错误。
- 重新启动 GUI 后，由 GUI 进程 18296 自动启动代理进程 5000，实际路径为完整的 `src/Picer.MediaAgent/bin/Debug/net8.0-windows/Picer.MediaAgent.exe`。GUI 日志记录自动启动成功，代理已收到服务配置并连接成功。
- 通过该代理对视频 7、8 执行真实远程分析，均返回 HTTP 200，分别检测到 1、2 张人脸；两次 `failedFrames=0`，分析后 GUI 和代理均继续运行。
- 代理路径选择及凭据回归 11 项全部通过。非 RuntimeComponent E2E 全套最终为 59/60；`PeoplePage_ShowsImmediatelyAndExposesClassificationWorkflow` 在 `UnitTest1.cs:456` 的人物页面 UI 元素定位断言失败，单独复核仍失败，未将本次验证描述为全套通过。该用例及人物页面代码未在本次修改，保留为独立待排查项。
- 首轮测试中的新增断言曾假定临时目录外没有其他代理；由于测试临时目录位于真实仓库下，解析器会找到真实开发目录。已修正为验证缺少 DLL 的候选被跳过。首轮模型空间窗口查找失败在后续全套及独立复核中通过。
