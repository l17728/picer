# 照片内容替换后的分类结果失效

状态：schema76 专项修复已通过回归，未发布部署。schema75 完整桌面 E2E 113/113、整仓单测 917/918；唯一失败为旧库夹具降级时未移除新触发器，已修正并复测。

## 根因与修正

照片分类字段已绑定内容，但搜索使用的分类断言和旧拒绝投影仍可能留在原引用上。文件替换后，新内容会继承旧照片的人工分类。另一个问题是模型分类投影优先使用 `ContentSha256`，重新索引期间它可能仍指向旧内容；现改用当前核实的 `Sha256` 决定内容级归属。

schema76 增加引用内容变化触发器：核实哈希变化，或未知内容的大小/时间变化时，清除本引用的旧分类断言、引用级拒绝、旧视觉投影和语义聚类成员。其他相同旧内容引用及其内容级决定保留。核实哈希未变而仅修改时间，不清除人工分类。

历史迁移清理已知内容不匹配的分类断言。schema75 的旧字段播种补充内容核对：旧断言明确属于另一哈希时，只可移交同来源仍持有该内容的引用，不能绑定到已替换的新照片。

## 验证

- `photo-classification-stale-projection-repro-schema75.trx` 0/2，已知/未知旧内容均复现新照片继承旧人工分类。
- `photo-classification-projection-invalidation-schema76.trx` 21/22：结果清除修复通过，剩余失败定位为模型投影使用旧 `ContentSha256`。
- `photo-classification-stale-projection-diagnostic-schema76.trx` 1/2，确认不是分类任务异常，而是旧内容拒绝阻止新内容生成分类。
- `photo-classification-current-content-regression-schema76.trx` 57/57，包含核实哈希优先、仅改变时间的保护、迁移及分类回归。
- `photo-face-legacy-fixture-retest-schema76.trx` 11/11，原整仓失败的旧库夹具修复后通过。
- `photo-classification-legacy-projection-restore-schema76.trx` 14/14，旧断言和字段共同移交尚存的原内容副本，替换后的新照片不继承旧分类。

不把这次局部修正视为剩余目录生命周期、其他元数据字段、压缩及整体双向同步已经完成。
