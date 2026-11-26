# 视频页分析状态显示修复（2026-11-26）

## 问题与根因

视频页“分析状态”列此前按已有结果动态拼接标签；当视频尚无关键帧、人脸、转写或摘要时，绑定属性返回空字符串。单元格和整列视觉上因此像丢失状态。远程页面还可能遇到服务端汇总缺失的情况，而视频记录本身已有的人物分析状态没有在所有构造路径中作为兜底。

## 修复

- 无任何分析结果的视频明确显示“未分析”，有关键帧但没有其他分析时显示“帧|未分析”。
- 列表汇总状态缺失时，使用视频记录自身的 `PersonAnalysisStatus` 兜底，保留服务端已有状态。
- 将状态列宽从 110 调整为 160，并更新提示文字，避免组合标签被挤压且明确解释未分析状态。

## 验收

- `AnalysisStatusText_IncludesOnlyAvailableFrameAndAnalysisStates`：5 个理论用例通过，覆盖无分析、关键帧、失败、转写和组合状态。
- Release GUI 构建：`dotnet build src/Picer.App/Picer.App.csproj -c Release --no-restore --warnaserror`，0 警告、0 错误。可执行文件 SHA-256：`88A42D3BD7FF0476CF538BAA0AD015E872094B45E1F3483873222103B7D626E9`。
- 新版本正式 GUI 已从 `src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe` 启动。

该修复只影响视频状态的可见性和布局，不修改索引或原始媒体。

