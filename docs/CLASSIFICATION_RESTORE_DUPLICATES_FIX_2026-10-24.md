# 重复照片撤销拒绝的恢复修复

状态：schema72 开发源码已修复并通过专项回归，尚未发布部署。

## 根因和行为

拒绝某个机器分类会影响同一来源下、具有相同已核实 SHA-256 的照片引用。原 `RestoreSuggestion` 虽然清除了内容级拒绝，却只将当前图片的机器断言恢复为待审核，且没有清除旧 `visual_concept_rejections`。因此两份相同照片中只有一份重新出现在搜索结果，另一份仍被拒绝。

修复在同一事务内清除两种拒绝记录，并将同一来源、同一已核实内容的机器断言恢复为待审核。无有效哈希只处理当前引用；不同来源的相同内容不受影响；独立人工确认的断言保持不变。此项修复不等于完成照片分类人工决定的因果同步，该部分仍按[补齐设计](PHOTO_CLASSIFICATION_CAUSAL_SYNC_DESIGN_2026-10-24.md)推进。

## 验证

- `classification-restore-repro-and-legacy72.trx`：分类回归复现失败，预期返回两份照片，实际仅返回一份；同批旧库身份迁移复测通过。
- `classification-restore-and-legacy72-retest.trx`：82/82，包括分类持久化、HTTPS/管道契约与旧库迁移。
- `classification-restore-video-person-lifecycle72.trx`：34/34，增加无哈希照片隔离及人工确认保留，并覆盖视频人物合并/删除。
- 严格整仓构建零警告、零错误；完整单测的最终结果另行记录，不用专项通过替代整仓验收。

## 交互契约追加检查

schema74 开发阶段对照定稿 UX，`classification-ux-repro-schema74.trx` 复现另外两个问题：机器拒绝的搜索过滤同时屏蔽了独立人工确认；恢复自动分类只改审核状态，仍保留 `automatic`，因此直接进入默认搜索。该批次 1/3，其中升级后身份持久性测试通过，两个分类用例失败。

已修改搜索过滤，使机器拒绝只限制机器来源；恢复同时将机器断言改为 `suggested/pending`，保留独立人工来源。正在严格构建及复测，不能将原 schema72/73 测试计为这两项修复的验证。

修复后 `classification-ux-search-retest-schema74.trx` 31/31 通过，包含两项失败复现、分类持久化、搜索及照片身份生命周期；`classification-owner-ui-schema74.trx` 的真实分类窗口审核/阈值流程 1/1 通过。后者验证既有窗口流程，没有将其计作尚未开发的分类因果冲突界面验收。
