# 检索与自动分类实现任务计划（2026-10-16）

本计划依赖[产品规格](SEARCH_AND_AUTO_CLASSIFICATION_SPEC_2026-10-16.md)、[UX 规范](SEARCH_AND_AUTO_CLASSIFICATION_UX_2026-10-16.md)、[技术 SDD](SEARCH_AND_AUTO_CLASSIFICATION_TECH_SDD_2026-10-16.md)及[最终实现裁定](SEARCH_AND_AUTO_CLASSIFICATION_DECISIONS_2026-10-16.md)。任务开始前冻结代码基线、类别标注方案、查询标注方案和性能硬件/SLO；不能用通过单测代替真实类别质量验收。

## 阶段 0：实现前门禁（文档评审通过；代码阶段已启动）

- [x] 产品/UX/技术文档经检索、视觉分类、WPF/UIA 专家审阅；本次修订的契约阻断项已关闭。
- [x] UX 拍板分类入口、机器/建议/人工状态、待确认搜索默认关闭、机器结果来源徽标和同类别批处理确认流程。
- [ ] 冻结有代表性的 Windows x64 机器和照片库规模，登记搜索、索引、分类、取消延迟 SLO。
- [ ] 建立版本化查询集及类别 taxonomy 标注指南；按事件/连拍/近重复隔离分类/检索评测样本。
- [ ] 冻结每个可自动归类语义类别的最小独立验收样本量、precision 误归上限、coverage 下限及置信区间标准。未填写或未达标的类别只允许建议。
- [x] 按专家一致意见启动阶段 1；四项已暂停的 P2 不纳入本计划。

语义类别自动启用、CLIP/RRF 默认启用仍受未冻结的独立 holdout、质量阈值和硬件性能 SLO 限制；开发期间只实现安全的数据结构、建议态 UX 和可关闭/可回退路径。未达门槛的语义类别保持待确认，RRF 保持 opt-in。

## 阶段 1：P0 状态正确性与安全数据模型

1. 将 schema 46→47；备份/暂存/回滚严格使用现有 `DatabaseMigrationSafety`。
2. 创建来源可区分的分类 assertions/rejections 及确定性分类投影；保留 tags/albums、legacy categories、脸部确认。
3. 将原视觉 `suggested` 迁为建议，不再默认参与文字问答、标签筛选或 AI 回答；旧 `accepted` 来源不明时显式保留 legacy-effective，不能谎称用户确认。
4. 提供增量分类投影服务，只查询索引表及已有分析；不遍历全目录、不散列/解码原图、不隐式发出云端请求。
5. 写来源隔离回归：模型重跑、拒绝跨模型版本、用户确认/拒绝优先、同名人工标签不被移除、批次失败及取消恢复。

## 阶段 2：P0 用户流程与 UX

1. 添加 `AI 功能 → 照片分类与审核…` 独立窗口，按 UX 文档实现首次空状态、待确认、自动归类、用户确认、未分类、已忽略、失败列表和后台进度。
2. 实现单张确认/改类/拒绝/撤销，以及同类别批量确认/拒绝、预览数量和批次撤销。
3. 搜索默认包括用户确认、metadata 和过质量门槛的自动分类；建议默认排除，显式打开后带状态徽标。
4. 从当前照片选择进入同一纠正操作；原图全程只读。远程模式无写权限时只显示明确不可用提示。
5. 添加 UI Automation 断言中文入口、状态、按钮、键盘路径和所有空/失败状态。

## 阶段 3：P1 检索基线与可选融合

1. 先报告当前 BM25-only 在固定 holdout 上的 Recall@K、Precision@K、nDCG/MRR、空结果率和延迟。
2. 新增无模型依赖的 RRF coordinator 单测与来源解释，不直接修改默认 AI 问答行为。
3. 为 CLIP repo 增加分块读取、流式 Top-K、批次 cancellation；评测 10k/100k 图及代表性硬件，必要时才测试有界并行或 ANN。
4. 在同一 photo-only 数据集上影子比较 BM25-only、CLIP-only、RRF；报告 query bootstrap CI、首屏精确率非劣界值和性能 SLO。
5. 只有预登记质量/性能门槛都通过后，才把 RRF 接入可选搜索开关；默认启用需另记评审决定。视频单独报告，不能把 CLIP 照片覆盖扩写成视频覆盖。

## 阶段 4：综合验证与交付

- `dotnet restore picer.sln`
- `dotnet build picer.sln -c Debug --no-restore --warnaserror`
- `dotnet test tests/Picer.Tests/Picer.Tests.csproj -c Debug --no-build --no-restore --filter "Category!=RuntimeComponent"`
- `dotnet test tests/Picer.E2E/Picer.E2E.csproj -c Debug --no-build --no-restore --filter "Category!=RuntimeComponent"`
- 对迁移使用隔离 v46 数据库 fixture 做完整备份、升级和恢复演练；不可连接用户本地照片库或生产 IndexService。
- RuntimeComponent CLIP 验证仅在固定离线资产环境跑对应标记测试，并明确报告确实执行的用例。
- 发布说明记录本地/远程模式边界、没有启动的付费调用、图库规模/硬件、检索及分类质量置信区间、性能 p50/p95 和 feature flag 状态。

## 专家审阅共识记录

定稿后记录每个领域的审阅版本、日期、阻断项和关闭依据。UX 必须在阶段 0 冻结导航与状态操作；检索专家确认 BM25/CLIP 对照公平；视觉分类专家确认拒识/逐类质量门槛；可靠性/安全专家确认 migration、撤销和隐私；UI 自动化专家确认键盘与无障碍断言。任何审阅仍有阻断问题时不得开始对应阶段编码。



实现冲突、来源隔离、内容级拒绝、自动分类白名单及任务恢复契约以[最终实现裁定](SEARCH_AND_AUTO_CLASSIFICATION_DECISIONS_2026-10-16.md)为准。
