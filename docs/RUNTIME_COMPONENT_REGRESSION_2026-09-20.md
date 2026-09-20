# RuntimeComponent 实机回归记录（2026-09-20）

## 资产状态

已核对 `third_party/runtime` 的固定清单。YuNet、SFace、FFmpeg、whisper.cpp（含 base-q5_1 模型）、WebRTC AEC3、Chinese-CLIP 和 Florence-2 INT8 文件均存在，文件大小与 `publish-self-contained.ps1` 及 `RuntimeComponentService.RequiredComponents` 中的 SHA-256 完全一致。运行时原生库 DirectML/ONNX Runtime 由 x64 构建输出提供，测试输出目录也已包含对应文件。

## 实际执行

命令：

```powershell
dotnet test tests/Picer.E2E/Picer.E2E.csproj -c Debug --no-build --no-restore --filter "Category=RuntimeComponent" --logger "console;verbosity=detailed"
```

结果：7/7 通过，耗时约 1 分 48 秒。覆盖：

- YuNet/SFace、FFmpeg、whisper.cpp 的离线自检与固定哈希校验；
- Florence-2 INT8 CPU 加载、视觉编码器推理及 DirectML 兼容探测；
- WebRTC AEC3 合成回声抑制；
- ModelServer 语音/VL 配置端到端契约。

## 测试套件定位

完整 `Picer.Tests` 回归此前被误判为“挂起”。开启 `--blame-hang --blame-hang-timeout 2m` 后确认：测试持续输出并逐项通过，未出现无输出超过 2 分钟的挂起用例；部分 SQLite/HTTP/文件操作压力用例单项需要数分钟，属于慢测试而非死锁。另修复了直接设置 `user_version=34` 的旧数据库 fixture 在 v37 迁移时缺少 `file_created_utc` 的问题，并将索引创建拆为独立语句。

