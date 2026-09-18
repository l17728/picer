# InsightFace 自适应模型空间实现记录

## 结论

人物模型下拉框中的 `自适应 InsightFace（YuNet 320/640 + ArcFace）` 已接入。这里的“自适应”明确指检测阶段使用 YuNet 320 与 640 的回退/合并策略，向量阶段使用 InsightFace ArcFace；两者不是把 SFace 向量与 InsightFace 向量混合比较。

## 行为

- 切换到 InsightFace 时，优先选择索引中 `adaptive:` 版本的人脸框作为裁剪来源。
- 缺少目标模型向量时，后台增量生成 InsightFace 向量，显示进度，不阻塞人物页面。
- 基线评估和手动“补齐 InsightFace 向量”使用相同的自适应检测框优先级，保证样本集合一致。
- SFace 与 InsightFace 在精度验证窗口中使用各自的校准数据、阈值和模型版本；向量不会跨模型比较。
- 已命名人物与“不分类”是跨模型共享的人工确认状态，自动归类结果仍按模型空间隔离。

## 限制

当前 InsightFace 的 ArcFace 向量由统一模型平台提供；客户端不会把 ArcFace 权重伪装成本地 SFace，也不会在本地修改 ArcFace 基模权重。若平台未提供 InsightFace 服务，切换会保留当前模型并给出失败原因。
