# 全双工 WebRTC AEC3 实施报告（2026-09-15）

## 结论

Picer 客户端已接入真实 WebRTC Audio Processing Module / AEC3，不再只依赖播放期能量门限。AEC3 作为设备侧低时延 DSP 在客户端进程内运行；ASR、TTS、VL 等模型推理仍统一通过 ModelServer 服务访问。

AEC 必须同步接收扬声器 render 流和麦克风 capture 流，每帧预算为 10 ms。把它放到远端 ModelServer 会引入网络抖动、破坏时序并使断网时无法对话，因此它不是“绕过统一模型平台”的 AI 服务，而是和声卡采集同生命周期的客户端媒体处理组件。

## 实现

- `tools/webrtc-aec3/` 保存稳定 C ABI、GN 目标和可重复构建脚本；WebRTC 源码不进入 Picer 仓库。
- 固定源码基线：WebRTC `69dfe169014664ef156db5a2599586b994b54ec7`，Windows x64 Release，SDK 10.0.26100.0。
- `picer_aec3.dll` 只导出创建、销毁、render、capture、版本 5 个 C 函数，隔离 WebRTC C++ ABI 变化。
- `WebRtcAec3Processor` 动态加载 `runtime/webrtc/picer_aec3.dll`；也支持 `PICER_AEC3_PATH` 供验收覆盖。
- TTS 播放流统一重采样为 16 kHz/16-bit/mono，通过 `EchoRenderWaveProvider` 按实际播放读取进度送入 reverse stream。
- 麦克风 40 ms 回调拆为四个 10 ms 帧，经 capture stream 原位净化后再做语音活动检测和 ASR 组包。
- DLL 缺失或加载失败时自动回退到原播放门限策略，程序仍可离线启动；日志只记录后端和错误类型，不记录音频、识别文本或人物信息。

## 制品与许可

- DLL：`runtime/webrtc/picer_aec3.dll`
- 大小：1,196,544 bytes
- SHA-256：`AC405193CB90F963177B883FDC1533660128B3C15CCA00986FBAFF0A215BC6DD`
- 许可：BSD-3-Clause，`runtime/licenses/WebRTC-BSD.txt`
- `RuntimeComponentService` 和 `publish-self-contained.ps1` 均执行大小及 SHA-256 门禁。

## 验证

| 用例 | 结果 |
|---|---|
| x64 Release 原生编译与 5 个导出检查 | 通过 |
| .NET 动态加载、创建、销毁 | 通过 |
| 700×10 ms 合成回声，80 ms 延迟、55% 回灌 | 通过 |
| 稳态输出/输入回声能量比 | `0.043102`（约降低 95.7%，13.7 dB） |
| 实机 FHD 1080P MIC + 默认扬声器，8 秒/780 帧 | `0.019429`（约降低 98.1%） |
| 全部离线组件大小与 SHA-256 | 通过 |
| AEC3 缺失时回退启动 | 由非 RuntimeComponent 回归覆盖 |

合成用例是确定性回归门禁，不替代真实房间验收。真实扬声器、麦克风距离、音量和用户插话仍按 `FULL_DUPLEX_DEVICE_ACCEPTANCE_2026-09-11.md` 的 FD-006 执行。
