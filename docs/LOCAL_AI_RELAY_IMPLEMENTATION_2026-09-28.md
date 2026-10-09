# Local AI Relay Implementation (2026-09-28)

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

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
