# Picer Model Server

统一模型服务端，与 `src/Picer.App` 客户端分离。负责远程元数据/index API、模型目录、VL/ASR/TTS 路由、GPU 调度和人脸训练任务生命周期。

Picer 客户端通过 HTTPS 读取和更新远程 index，不依赖服务端本地文件路径或 SQLite 结构；模型推理同样只通过服务 API 调用。客户端本地缓存不可作为远程事实源，断网时只能显示最近一次缓存并明确标注离线状态。

当前提交的是可运行的 API 骨架：训练执行器和制品签名需要接入实际 GPU worker 后才会产生真实制品，服务不会伪造训练结果。
