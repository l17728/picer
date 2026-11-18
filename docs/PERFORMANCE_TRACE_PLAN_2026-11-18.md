# 目录描述与媒体打开性能采集（2026-11-18）

## 目录描述

`MediaDescriptionBatch` 记录运行总耗时、发现/处理/跳过数量、每个项目耗时和失败阶段；`PhotoInputAnalyzer` 记录准备输入、原图读取、复制、校验、模型调用及清理各阶段耗时；模型客户端记录预处理、响应头、响应体和总请求耗时。

## 双击预览与原图

`PlayableMediaCache` 记录缓存命中、并发合并等待、实际下载字节数、排队耗时、下载耗时和总耗时；`RemotePhotoPreview` 额外记录 1600 像素预览解码耗时；流式原图记录每个媒体块的排队和传输耗时。

## 日志位置

- GUI：`%LOCALAPPDATA%\\Picer\\logs\\picer-YYYY-MM-DD.log`
- 驻留程序：`%TEMP%\\picer-media-agent-diagnostics\\picer-YYYY-MM-DD.log`

日志只记录 ID、阶段、耗时和字节数，不记录路径、凭据、问题文本或图像内容。
