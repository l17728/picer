# 发布前验收操作手册（RELEASE_READINESS 五项）

日期：2026-09-09
依据：[RELEASE_READINESS_2026-09-07.md](RELEASE_READINESS_2026-09-07.md)。五项均为**外部环境验收**，无代码缺口；本文把每项转为可执行流程。全部通过后发布状态才从"代码就绪，环境待验收"改为"可发布"。

## 通用前置

```powershell
git pull && dotnet build picer.sln -c Debug --warnaserror
dotnet test tests/Picer.Tests/Picer.Tests.csproj -c Debug --no-build --no-restore --filter "Category!=RuntimeComponent"
dotnet test tests/Picer.E2E/Picer.E2E.csproj -c Debug --no-build --no-restore --filter "Category!=RuntimeComponent"
./tools/publish-self-contained.ps1   # 产出 GUI 与 Picer.IndexService 双自包含包
```

每项验收产出一份带日期的记录（命令输出 + 判定 + 执行人），追加到本文末尾。

## 1. 统一训练平台服务端部署、鉴权、任务恢复与联调

前置：一台可 GPU 训练的服务器；`Picer.ModelServer`（或正式训练执行器）可启动。

1. 服务器：`PICER_MODEL_SERVER_API_KEY=<强随机>` 部署 ModelServer，前置 HTTPS 反向代理。
2. 客户端：AI 设置中填训练端点 + 保存 API Key（入 Windows 凭据管理器）。
3. `tools/Picer.Benchmarks` 端点基准：确认 P50/P95 可达。
4. 鉴权：错误 Key 必须返回 401；正确 Key 提交训练任务 → 轮询日志 → 取消 → 重启服务后任务状态恢复。
5. 判定：任务全生命周期（提交/运行/取消/恢复）无伪造结果；` PLATFORM-OBS-001` 制品哈希与签名字段非空。

## 2. Qwen3-ASR-1.7B 与 Whisper 真实视频对照

前置：语料 ≥20 条真实视频（普通话/方言/低音量/背景音乐/多人交叠/长静音各 ≥2 条），`PICER_FFMPEG_PATH` 可用。

1. 同一视频分别走 Qwen3-ASR 服务端点与本地 Whisper（App 内已双链路）。
2. 记录：字错率、整句漏检、重复幻觉、耗时、峰值内存（任务管理器/`dotnet-counters`）。
3. 判定：Qwen3-ASR 字错率不劣于 Whisper 基线 5%；VAD 空结果必须触发整段兜底（观察日志）；内存不超设计预算（描述模型 6 GB 之外单独核算）。

## 3. Qwen3-VL 与中文 TTS 实机启动及资源预算

1. 按 `UNIFIED_MODEL_PLATFORM_ROUTING` 启动 VL/TTS 服务，客户端诊断中心（"AI 问答/中文 TTS"探针）全绿。
2. 记录：冷启动时间、常驻显存/内存、单次描述与单句 TTS 端到端延迟。
3. 判定：16 GB 内存 + 2 GB 显存预算内可同时待机；超预算项记录并列入限制条款。

## 4. WebRTC AEC3、真实麦克风/扬声器全双工

1. 外放扬声器 + 真实麦克风，开启全双工会话（`FullDuplexVoiceSession`）。
2. 用例：播放 TTS 的同时说话 → 回声不得触发误识别；静音期说话必须拾取；打断 TTS 必须即时停播并转向新轮次。
3. 判定：误识别 0 次/10 分钟；打断响应 < 1 s。客户端已接入 WebRTC AEC3；若未达标，记录 AEC 后端、设备、音量、麦克风距离和失败时间点，禁止仅靠提高门限掩盖问题。

## 5. 用户真实人物数据规模

1. 对真实图库执行 `FaceTrainingSnapshotService.CheckEligibility`（后续版本接入 UI；当前可临时调用）。
2. 判定：≥8 位已确认人物、每人 ≥5 个独立泄漏组、困难负样本覆盖；不足项即为标注工作清单。
3. 达标后以真实数据构建快照（manifest_hash 存档），再走训练提交联调（第 1 项）。

## 记录模板

```
### 验收记录：<项 N>
日期/执行人/环境（机器、系统、服务版本）：
执行命令与输出摘要：
判定：通过 / 不通过（原因与限制条款）
```

### 已完成的自动化/实机记录

- 2026-09-15：WebRTC AEC3 原生构建、动态加载、合成回声和运行时哈希门禁通过；FHD 1080P MIC + 默认扬声器实机探测 8 秒、780 帧，AEC 前后能量比 `0.019429`（约 98.1% 抑制）。
- 2026-09-15：270 项单元/集成测试、35 项 WPF E2E 和 GitHub Actions `34936024643` 全部通过。
- 2026-09-15：统一模型平台真实模型冒烟通过：Qwen3-VL 返回 `service ready`；Qwen3-ASR-1.7B 对真实 WAV 返回非空英文转写；中文 TTS 生成 61,996 字节 WAV。
- 2026-09-15：通过 8090 `/picer` 代理提交 4 样本 GPU 训练，任务完成、训练/验证准确率 1.0、FAR/FRR 0、ECDSA 签名与 SHA-256 均存在。
- 限制：以上实机探测不包含真人插话；FD-006 的连续 10 分钟零误识别和小于 1 秒打断仍需人工完成。
