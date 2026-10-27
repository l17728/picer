# 照片分类人工决定的因果同步实现

状态：schema75、同步元数据协议 9，核心与来源端 HTTPS 往返已接入，生命周期和完整 GUI 验收仍在进行，未发布部署。设计见[内容身份与分类字段约束](PHOTO_CLASSIFICATION_CAUSAL_SYNC_DESIGN_2026-10-24.md)。

## 当前核心

人工确认使用 `photo-classification-confirmation`（`1/0`），机器策略使用 `photo-classification-policy`（接受、拒绝、恢复待确认、撤销机器归类）。两者独立，机器拒绝不得抹除人工确认。操作绑定 schema74 的核实内容或持久文件版本，规范字段版本按来源和内容归并；重复引用不能各自覆盖并发版本。

确认、拒绝、更正、恢复和撤销机器归类进入业务事务内的字段记录与投影。字段读取、因果冲突及幂等请求复用既有元数据通道；补充照片分类的中文冲突显示。模型重新投影后重放该照片有效的人工字段，避免刚恢复为待确认的结果被新模型自动升为搜索命中。明确清除引用时，绑定移交给同来源的尚存引用；最后引用清除时清理字段、回执和冲突。

协议 9 的照片批次携带持久文件版本，服务端按发布者保存本地身份与服务端身份映射；上行字段、分页下行字段都转换无哈希身份及动态聚类概念键。范围暂停后重置下载水位，恢复时补齐更新。快照应用后重放有效人工字段。远程 GUI 的分类按钮直接调用服务端分类 API；人工确认、拒绝、更正、撤销、恢复需要 `index.write`，运行 AI 仍独立要求 `analysis.run`。

已补历史字段播种：只导入人工确认、显式审核与拒绝证据，不把普通机器建议当成人工决定。哈希补齐时相反决定保留为冲突，并将本地未确认提案上传空间供远程审核。协议基线已提高，旧来源端需重新发布包含照片身份的基线。完整来源认领/镜像、桌面冲突操作及整仓验证仍需以最后结果为准。

同一引用换内容前后的分类字段分别绑定原内容和新内容；原内容仍有同来源副本时移交其字段，避免新编辑覆盖旧决定。相互交换内容的引用也受同一规则保护。

## 验证

- `photo-classification-causal-repro-schema74.trx` 0/3，确认旧实现没有人工分类字段、不能使用统一因果版本，也没有对应字段移交。
- `photo-classification-causal-initial-schema75.trx` 19/19，包含新核心三项行为及原分类持久化回归。
- `photo-classification-replay-and-cache-schema75.trx` 64/64，覆盖模型重新投影、冲突中文显示与选择、清除原引用后重试、回执清理及内容缓存恢复；分类窗口专项 1/1。
- `photo-classification-https-initial-schema75.trx` 0/2：新身份查询把自动继承的 SQLite 事务清为 null，上传 HTTP 500。修复后上传成功。
- `photo-classification-https-transaction-retest-schema75.trx` 0/2：人工分类 API 错误要求分析权限，编辑设备得到 HTTP 403。修正上述五项人工操作的权限映射。
- `photo-classification-https-authorization-schema75.trx` 34/34，包含真实 HTTPS 有/无哈希往返、动态分类键、范围暂停恢复、无回传循环和权限规则。
- `photo-classification-content-replacement-repro-schema75.trx` 0/1，复现换内容后覆盖旧照片副本的决定；修复后 `photo-classification-network-lifecycle-schema75.trx` 82/82，包含交换内容及网络扩大回归。
- `photo-classification-hash-conflicts-schema75.trx` 15/15，含哈希补齐相反决定及人工冲突选择。
- `photo-classification-legacy-seeding-schema75.trx` 28/28，含有/无哈希旧库分类播种、来源隔离与其他迁移回归。
- `photo-classification-hash-conflict-https-schema75.trx` 2/2，真实 HTTPS 下本地哈希冲突上传、远程选择、来源回执闭环；另验证有分析权限但无索引编辑权限的设备不能修改人工分类。
- schema75 完整非 RuntimeComponent 单测 917/918；唯一失败为旧库夹具遗漏撤销新增触发器，修正后专项 11/11 通过。完整桌面 E2E 113/113，含两个新增照片分类冲突窗口场景。后续源码仍须重新完成整仓验收。

## 同期回归修复

schema73 完整单测为 892/893（35 分 3 秒），唯一失败是 `ContentDerivativesSurvivePhysicalDeletionAndRestoreToAnotherCopy`。根因是新缓存清除触发器把低层物理引用删除也当成显式索引清除。现将最后来源人物缓存清理移入 `ImageRecordPurger`，并由 schema75 删除旧开发版触发器；保留低层删除后的内容恢复语义。原失败测试保持原断言，在上述 64 项回归中通过；这不等同于 schema75 整仓回归完成。
