# Local AI Relay Implementation (2026-09-28)

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 历史记录：本文保留原始日期的实现或评审结论。当前正式版为 v1.5.1/schema 46；生产部署和账号模式启用边界见 [开发计划](DEVELOPMENT_PLAN.md)、[SDD](SDD.md) 与 [v1.5.1 部署记录](DEPLOYMENT_V1_5_1_2026-10-14.md)。四项外部质量/硬件 P2 验收仍暂停。
<!-- /historical-status-2026-10-10 -->

## Corrected topology

There is no local IndexService. Both Local and Remote Picer clients connect to
the same IndexService. Local mode reads its own SQLite/index and original media;
AI and media-model work is relayed through IndexService to the source-machine
MediaAgent/ModelServer.

## Implemented

- Local vision, scene summarization, AI chat, ASR, TTS, face analysis, FFmpeg
  probe/keyframe/poster operations resolve a paired IndexService endpoint.
- Direct local ONNX, Whisper, Qwen, loopback TTS, and FFmpeg fallbacks were
  removed from AppServices. Without a paired endpoint the capability is
  reported unavailable instead of silently calling localhost.
- `ServiceFaceAnalyzer` and `ServiceVideoMediaProcessor` implement the Core
  contracts and validate service responses; HTTPS SPKI pinning is respected.
- Incremental metadata sync recomputes its fingerprint after scene snapshots
  are materialized, making the second identical push idempotent.

## Verification

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`: passed, 0 warnings.
- Face service contract tests and incremental-sync idempotence test: 6 passed.
- The complete non-Runtime suite still contains an existing long-running test
  group; it must be run by class/filter for bounded CI diagnostics.

## Deployment requirement

The paired MediaAgent must register the required `PICER_LOCAL_*_ENDPOINT`
capabilities. Missing endpoints remain `unavailable`; they are never shown as
ready and never trigger a client-side model fallback.
