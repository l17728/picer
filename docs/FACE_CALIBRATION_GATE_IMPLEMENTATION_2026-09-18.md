# 人物识别校准联合门禁实现记录（2026-09-18）

本次把校准 Agent 的“可采用”判定从单一准确率扩展为联合门禁：

- 验证准确率 ≥ 90%；
- FAR（误接受率）≤ 1%；
- FRR（拒识失败率）≤ 10%。

用户操作产生的排他负样本随训练数据提交到 ModelServer。负样本不参与人物原型/质心构建，只用于评估最相似原型是否被错误接受；因此连拍重复或相似人物不会污染正类原型。GPU worker 和 CPU fallback 使用同一语义，超过门禁的制品不进入可持久化制品目录，但训练记录、指标、配方和数据集指纹仍归档。

Agent 仍执行最多 9 轮，每轮以历史记忆、人工纠错日志、时间跨度、姿态/光照覆盖和去重策略调整配方；每轮继续记录 FAR/FRR，所有通过联合门禁的制品保留并生成经验摘要，最终只选择本轮/历史最高且通过联合门禁的制品应用。

验证：`dotnet build picer.sln -c Debug --no-restore --warnaserror` 通过；GPU worker 通过 `python -m py_compile server/Picer.ModelServer/worker/face_worker.py`。
