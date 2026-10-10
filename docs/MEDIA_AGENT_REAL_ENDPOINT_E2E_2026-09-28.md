# MediaAgent 真实端点验收记录（2026-09-28）

<!-- historical-status-2026-10-10 -->
> **历史资料，2026-10-10 状态对照**：正文按原报告阶段阅读；其中“当前”“待办”“已部署”、测试数量及部署位置不是现版本的完成承诺或新任务授权。
> 当前阶段为 v1.3.5 已发布并部署、schema 45，约定代码开发已收尾；最新能力与验收范围见[当前能力](CURRENT_CAPABILITIES_2026-10-09.md)、[发布与媒体验证](MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)及[开发计划](DEVELOPMENT_PLAN.md)。四项外部质量/硬件 P2 仍暂停。
<!-- /historical-status-2026-10-10 -->

## 范围

本次验收覆盖统一 IndexService → MediaAgent → 本地模型/媒体运行时的真实链路。客户端不直接访问模型端点；端点配置由 IndexService 广播给在线 MediaAgent。

## 配置

| 能力 | 实际端点/运行时 | 状态 |
| --- | --- | --- |
| FFmpeg | `D:\vl-models\ffmpeg\ffmpeg-n9.0-latest-win64-lgpl-9.0\bin\ffmpeg.exe`（同目录 ffprobe） | 已验证 |
| ASR | `http://127.0.0.1:8001/v1/audio/transcriptions`，Qwen3-ASR-1.7B | 已验证 |
| VL | `http://127.0.0.1:8000/v1/chat/completions`，Qwen3-VL | 已验证 |
| TTS | `http://127.0.0.1:8090/api/tts/speech`，中文 sherpa-onnx | 已验证 |
| 人脸 | `http://127.0.0.1:8092/api/face`，YuNet + SFace ONNX | 已验证 |

MediaAgent 对模型端点统一注入本地模型 API key；TTS 在代理边界将通用 `text` 转换为实现所需的 `input`，并正确处理 WAV 二进制响应。

## E2E 结果

- `/api/service/v1/capabilities`：8 项能力均为 `ready`，执行节点均为 `media-agent`。
- 人脸 `face/detect`：真实缩略图返回 1 张脸，置信度约 `0.9363`，返回有效 128 维 embedding。
- TTS：真实中文文本经 IndexService 中转返回 WAV，验收样本约 6 KB。
- VL：真实 Qwen3-VL 请求经中转返回有效 OpenAI-compatible 响应。
- ASR：真实 WAV 经中转返回 `transcript.text.done` 和识别文本。
- 视频 probe：真实 MP4 返回 640×360、3 秒元数据。
- 视频关键帧：无场景切换的视频使用首帧回退策略，返回 1 个关键帧；不会再错误报告 `ffmpeg_keyframes_empty`。
- 视频音频抽取：返回 WAV，约 96 KB。
- 视频转写：返回 Qwen3-ASR 转写 JSON。
- GPU Worker：使用 `C:\Python312\python.exe`、CUDA 开关和 `face_worker.py`，通过独立 ModelServer 端口完成 `insightface-arcface` embedding 请求，返回有效向量。
- 流式 TTS：通过 `/api/service/v1/tts/stream` 返回 WAV，HTTP 200，响应头为 RIFF，实测约 23 KB。
- 流式 VL：通过 `/api/service/v1/vl/stream` 返回多段 SSE `data:`，包含增量 `stream`/`ready` 内容和 `[DONE]`。
- 自动化脚本 `tools/verify-media-agent-e2e.ps1` 已执行通过：8 项能力、真实人脸检测、流式 TTS、流式 VL 全部通过。

## GPU Worker 长时与恢复

生产 ModelServer（8091）已使用 CUDA Worker 配置完成 4 个并行 InsightFace embedding 请求，4/4 成功；改造前并行启动独立 CUDA 进程会显存不足，现由常驻 `GpuEmbeddingService` 单进程串行化 GPU 队列，避免 OOM。客户端取消后再次请求仍成功，证明服务可恢复。

## 搜索性能基准（Release）

`Picer.Benchmarks` 在当前机器运行 20 次选择性搜索：

| 数据量 | P50 | P95 | 最大值 | 结果 |
| ---: | ---: | ---: | ---: | --- |
| 10,000 | 28.2 ms | 39.0 ms | 41.7 ms | 通过 150 ms 门禁 |
| 100,000 | 128.3 ms | 205.7 ms | 223.3 ms | 超过当前 150 ms 观测门禁，需后续在目标硬件继续优化/复测 |

100,000 条记录的基准结果已归档，未将未达标数据误报为通过。

测试媒体位于 `D:\PicerData\media-agent-e2e`，不修改用户原始照片和视频。

## 代码修复

1. IndexService 使用 Web JSON 默认值解析 MediaAgent 的 camelCase 人脸结果，避免合法结果被错误判定为 schema invalid。
2. MediaAgent 为 TTS/VL/ASR/Face 请求注入模型服务认证头。
3. MediaAgent 兼容通用 TTS `text` 字段与 sherpa `input` 字段，并保留 WAV 二进制。
4. FFmpeg 场景检测无结果时回退抽取首帧，保证普通视频可浏览和后续分析。

## 运行时说明

本机 `third_party/runtime` 中 YuNet、SFace、FFmpeg 资产均存在且可加载；GPU Worker 使用实际 CUDA provider 完成一次 embedding 验收。完整 RuntimeComponent 测试仍应在发布机按固定资产 SHA-256 清单执行一次，不能以 CI 的非 RuntimeComponent 套件替代。
