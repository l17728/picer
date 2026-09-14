# FACE-TRAIN-004 实现记录

## 已完成

- ModelServer 增加 `POST /api/face-training/recipe/enrich` 接口。
- 接口使用 `face-evidence.v1` 协议，接收并返回 VL 属性集合，供后续统一模型平台的 VL 服务填充。
- 未提供属性时返回空集合，保持训练任务可离线运行，不阻塞既有数据配方预览。
- ModelServer 在 `--warnaserror` 下构建通过。

## 后续

当前接口完成协议边界与兼容性，尚未在本阶段内置具体 VL 推理引擎。实际推理由统一模型平台按服务配置执行，客户端只提交证据和协议版本。
