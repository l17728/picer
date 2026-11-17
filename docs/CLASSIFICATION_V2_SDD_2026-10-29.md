# 家庭照片多维分类 V2：技术 SDD

状态：v1.0 设计定稿，架构/AI/UX-QA 独立代理评审及修订复核通过，允许启动隔离 M0。2026-10-29。生产仍为 schema92；功能实现和验收状态另行记录。

## 1. 架构与信任边界

Picer.Core 提供预设目录、标签图校验、分类证据、增量计划、聚合查询及结果投影。App 负责浏览和授权，IndexService 在账号模式下是任务/标签图/结果的权威执行端，MediaAgent 只读取授权范围内的已核验图片，ModelServer/现有模型适配器提供视觉语言推理。本地模式复用同一 Core 契约，单机索引为权威。GUI 镜像不能自行创建第二个远程权威任务。

读取快照只 SELECT，不隐式恢复或插入派生记录。模型请求、媒体传输和群组计算全部在数据库事务外；领取任务、结果发布及水位推进使用短写事务。索引/分析不改原件，不因文件暂不可读自动清除引用。

## 2. 标签图与稳定身份

空间级标签字典，不与某个来源的人工决定混为一张表：

| 概念实体 | 关键字段与约束 |
| --- | --- |
| taxonomy_label | label_id、dimension、name、definition、origin(preset/dynamic)、state(proposed/active/retired/merged)、definition_revision；preset ID 不随改名改变 |
| taxonomy_alias | label_id、normalized_alias、locale；同维度歧义别名不能自动合并，返回候选 |
| taxonomy_edge | parent_id、child_id、kind(is_a/browse/related)、edge_revision；is_a/browse 各自及其浏览联合图无环，related 不参与祖先筛选 |
| taxonomy_change | operation_id、base_revision、causal_version、actor、decision、payload；改名/合并/移动需 CAS，冲突进入既有人工冲突流 |
| taxonomy_proposal | proposal_id、候选定义/归属、候选证据引用、模型/提示版本、status、review_revision；幂等合并重复建议 |

标签和边由服务端校验，不能执行模型生成的 SQL、路径、URL或任意操作。标签存在性、维度、最大深度/节点数、环、重复边、无效父级、重复候选、规范化同义关系均有校验。LLM 只推荐，程序强制约束。候选新增不自动改动已生效图。

`is_a` 为类型包含，例如露营属于户外活动；`browse` 为明确批准的多位置导航，例如露营可展示于旅行活动入口；`related` 表示常见共现，例如森林与露营。后者不使全部露营照片匹配森林。父级筛选展开 active 的 is_a/browse 后代；跨位置始终复用同一 label_id，不复制分类记录。

合并保留旧 ID redirect 与历史证据，旧筛选重定向到规范 ID，循环 redirect 禁止。仅展示位置/名称调整不重跑推理；定义含义改变使用 definition_revision 并重新评估受影响的照片，不能把旧定义证据悄悄重解释。

## 3. 媒体身份、证据和人工保护

复用空间 epoch、publisher/owner 及可信内容 SHA-256；无可信哈希时使用现有持久文件版本与引用身份，不能仅用可复用整数 ID 或文件路径。跨来源同哈希可复用获授权的机器推理制品，但人工确认/拒绝仍限其来源归属。来源撤权、索引清除和文件内容变化会使旧工作项失效。

分类输入包含当前图片的核验标识、受限分析图、有效描述版本 ID 及载荷摘要、人工确认身份的证据版本、允许使用的元数据摘要、模型/预处理/提示版本和有关标签定义版本，以及发现水位/候选召回策略版本。描述历史与当前支持证据按 §12.1 区分，已撤回或被人工更正替代的版本不用于当前支持；仅按稳定 ID/摘要精确去重，冲突保留来源，不靠客户端时间或同义判断选择真相。描述迟到触发新一轮增量。

所有描述与 OCR 均作为不可信材料封装，明确不得当作指令。LLM 输出严格结构化：选中的 label_id、证据引用、支持/冲突/不确定状态、动态类别 proposal。拒收不存在的 ID、非本次输入的证据引用、超限输出及身份猜测。人物身份来自人工确认记录；未命名的检测人脸只能支持“人脸证据/有人候选”，不能生成姓名、亲属关系或敏感属性。

人工确认、拒绝、机器结果和处理策略分开保存。更新机器结果不能删除人工标签；拒绝同来源同内容的标签持续生效，恢复后回到待确认。现有 `photo-classification` 因果字段、归属与重放规则必须保留（见 10 月 24 日设计），不新增仅靠时间戳取最新的通道。发布机器结果前重新读取并重放人工策略，防止推理期间用户修改被覆盖。

## 4. 增量指纹与扫描

`input_fingerprint = SHA256(canonical(version, space_epoch, owner, media_identity, descriptions, people_evidence, allowed_metadata, model, preprocessing, prompt, relevant_label_definitions, taxonomy_discovery_revision, candidate_policy_version))`。集合按稳定 ID 排序、精确去重，使用 §12.1 的规范编码。本地路径不是可信内容的共享键。标签展示顺序、别名或模型不使用的元数据不纳入推理指纹。

持久 `classification_work_state` 保存 last_success_fingerprint、last_attempt_fingerprint、outcome、retry_after、依赖版本、当前制品及提交修订号；只在结果事务真正成功后推进 last_success。失败、不支持输入、无描述、结果为空必须区分；无标签但完成有效推理可记为 analyzed_unclassified，不能假装未处理，也不能算有内容覆盖。

增量原因枚举：new_media、media_changed、description_changed、person_evidence_changed、metadata_changed、model_changed、prompt_changed、taxonomy_definition_changed、retry_due、interrupted。一个照片多个原因只创建一个工作项。已有未完成租约不重复入队；完全未变化且 last_success 匹配则跳过。改变定义影响使用该定义的结果、其候选群及之前未识别照片；扩充类别时使用版本依赖索引生成有界重评计划，不在打开界面时重跑全库。

扫描从变更日志/索引读取，明确 `scan_high_watermark` 与 scope_revision，keyset 游标递进，日志压缩后回退全量比对轻量指纹；不枚举原始目录。预览计划带 plan_id、scope_revision、input_watermark、expires_at；执行前检查授权/范围/预算与版本，过期返回 plan_stale。扫描期间的新事件进入下一批，完成文案明确截止水位。

## 5. 持久任务和模型运行

job: planned→queued→running→pausing→paused / completed / partial_failed / cancelled；item: pending→leased→analyzing→committing→succeeded / analyzed_unclassified / failed / superseded / cancelled。持久租约 owner、expires_at、attempt_id、防重 request_id；重启只回收过期租约。暂停不领取新项，在途结果安全提交；取消传播 token，已经提交的结果保留。提交后响应丢失用 receipt 查询，不重写结果。

推理与动态聚类按用户模型配置、数量/费用/并发预算执行，离线不自动外发。超时/网络暂态错误有界退避；只读库、无授权、输入身份变化不盲目重试。推理请求无法承诺恰好一次收费，使用预留预算、request_id、制品缓存及服务商回执尽量避免重复；预算耗尽暂停，保留明确原因。

每次提交重新核验空间/引用存在、身份、输入指纹、定义版本、人工策略及任务租约。同一输入结果幂等发布；过期输出可留作受控诊断制品但不投影到当前分类，重新排队。完成计数从持久项状态汇总，不以遍历游标代替成功计数。

## 6. LLM 动态聚类

先使用同模型版本的现有向量/标签/描述召回有限候选组，LLM 根据代表图和证据判断是否有可命名共同主题、是否与现有类同义、是否可细分及归属位置。向量距离不直接定义父子关系。代表图上限、候选组大小与每日候选数可配置；首期建议每组最多 24 张、代表图最多 8 张，候选池分批可取消。

模型返回共同主题及支持成员证据；未发送/未验证的组成员不能因代表图相似而自动全部贴标签。新类别先 proposed，确认定义后逐项验证成员，已有人工标签不被群体结论覆盖。组合场景优先复用多标签，例如雪地+婚礼，不为每个笛卡尔组合新增类别。罕见但真实内容仍保留待细分，不为凑群组强行归类。

## 7. 浏览查询与指标

浏览请求含 scope、include_label_ids、exclude_label_ids、match_mode(all/any)、include_suggestions、taxonomy_revision、snapshot/cursor、page_size。先展开父级/redirect 为标签集合，再应用来源授权与内容有效性；同一选中父级内部为 OR，不同选中项按 all/any 组合，排除最后应用。空包含条件表示全部授权照片，空 any 也不能返回空图库。包含与排除冲突时排除优先并给出提示。排序包含稳定 ID。

节点数/命中总数均 COUNT DISTINCT 当前媒体引用 ID；同照片多标签/多浏览父级不重复。不同来源同内容的引用默认保持不同照片项，与既有图库计数一致；另显示可选内容去重数，不能静默换口径。节点静态范围计数与当前组合命中计数分别标注；分页、聚合与预览使用同一授权快照和 taxonomy_revision，过期游标显式刷新。

覆盖指标：有效照片总数；至少一个有效内容标签的去重照片数（分用户确认/已验证机器自动/建议）；无有效内容标签；未处理；待更新；处理失败；已分析但未识别；描述缺失；有检测人脸/有确认身份。元数据字段、相似群组占位标签均不计作已识别内容。处理状态主分区互斥，描述缺失/人脸覆盖为可重叠维度。每页返回数、全量关联条数、不同标签数、去重照片数不能混淆，禁止拿 limit=500 的返回数组计算全库总数。

## 8. API 与同步草案

采用版本化 `classification/v2`，实际 route 绑定延续现有账号/来源授权和证书固定，不通过另建绕过鉴权的端点实现。

| 操作 | 契约 |
| --- | --- |
| GET taxonomy | snapshot revision、labels、typed edges、redirects；支持 since_revision 增量、删除 tombstone 和重建基线 |
| GET coverage / POST browse | 授权 scope、相同 snapshot 与 taxonomy_revision；返回全量聚合、当前页和 opaque cursor |
| POST incremental-plans | request_id、scope、策略；返回冻结计划与原因去重计数，不调用模型 |
| POST jobs | plan_id、request_id、启用配置引用、预算上限；返回 durable job_id；同 request_id 幂等 |
| GET jobs/{id} / POST pause,resume,cancel | 持久项统计、错误原因、安全重试提示和 checkpoint |
| POST proposals/{id}/review | expected_revision、accept/merge/reject、typed parents；冲突 409，不覆盖另一客户端决定 |

人工与 taxonomy 结构修改复用因果版本基础设施；机器输出按输入身份和指纹同步，不把同一结果旧副本当新人工修改。local/offline 编辑有 durable outbox、server receipt、快照回读确认三阶段；上传成功不立即删除本地证据。publisher 只收发授权来源断言，GUI 镜像可读整个授权空间，但不能跨来源复制人工决定。整棵标签图和分类投影需同一基线/增量边界，依赖缺失先暂存，不丢弃。

旧客户端继续读取旧分类投影；V2 新增类型和定义不得未经映射塞进旧字段改变既有语义。协商缺少 V2 能力时仍可只读旧视图，并明确新功能不可用。数据库新增表/索引使用实施时分配的连续 user_version 迁移，先关闭写入者、整库 DB/WAL/SHM 备份，遵循 DatabaseMigrationSafety。禁止本阶段对正在运行的生产库试迁移。

## 9. 可观测性与性能

按 scope 筛选、快照获取、taxonomy 展开、SQL/聚合、缩略图、模型排队/首响应/总耗时、结果提交和同步回读分别记时。只记录 ID、数量、错误码和时长；不记录姓名、描述、用户问题、图片、向量或凭据。限制每页 200、标签节点分支按需加载；缩略图复用现有可见优先缓存/原图授权读取，取消旧查询并丢弃过期返回，不能一次加载全库原图。性能目标和质量门禁见验收文档，尚未测得的数据不得写成通过。

## 10. 模型输出约束示例

```json
{
  "schemaVersion": "classification-result-v1",
  "inputFingerprint": "server-issued-input-id",
  "labels": [
    {"labelId": "scene.forest", "evidenceIds": ["image:current", "description:v17"], "support": "supported"}
  ],
  "uncertainties": [{"labelId": "activity.camping", "evidenceIds": ["description:v17"], "reasonCode": "image_text_conflict"}],
  "proposalIds": []
}
```

`inputFingerprint` 只回显服务端标识，不采信模型自行构造的输入身份。图片和描述冲突不得仅由“模型说置信高”解决。模型支持类型包含 supported/uncertain/contradicted；程序把受保护人工标签与机器建议分别投影。文字理由可在本地/受控空间展示，但不是结构校验依据，不进入普通日志。

群组输出限定候选组 ID、已见成员 ID、共同类别/候选定义、typed parents、支持证据及不支持成员；无法看完所有成员时明确 returned_verified_members。接受群组名称不允许把整个候选池批量标为正确。JSON 校验失败记为模型输出失败，不写部分标签、不推进成功指纹。

分批子范围无匹配只记录该范围已完成；媒体级 succeeded_unclassified 必须等当前全部要求范围与发现水位均完成，且不存在其他有效支持标签，不能由单个空子批改写全局状态。

## 11. 初轮评审补充契约（v0.2）

### 11.1 新类发现与重新解释

在“相关定义版本”之外增加 `taxonomy_discovery_revision` 与 `candidate_policy_version`。新增/启用类别、更改定义使其可能匹配新成员时推进发现水位，标记授权范围中所有适用照片为潜在待重评；优先使用向量挑选处理顺序，但不能把没被向量选中的照片当作已验证不受影响。只有可证明不改变匹配语义的别名/浏览位置变化不推进发现水位。UI 在用户开始前显示可能影响的全量去重数，预算不足可分批继续，不能因为新类没有历史命中而永久漏掉它。

旧成功指纹不因推进水位被擦除，新增 pending obligation 独立持久化；未覆盖当前发现水位的照片不能报告“完全最新”。模型/预处理版本及类别定义作用域变化采用相同机制，输入指纹含 discovery_revision/candidate_policy_version；不得假设新的向量阈值与旧模型空间通用。

### 11.2 图权限与人工决定映射

预设字典可公开给空间成员；动态候选名称/定义本身可能暴露图片内容，按生成证据的来源权限限制可见性，不以“某一成员可见”推出整个候选可见。跨来源候选需具备所有参与来源的使用权限；空间共享提升是显式操作，需符合现有角色/来源授权。图节点、边、别名、提案、聚合、缓存键和游标均绑定 scope_revision；过滤隐藏节点同时移除其边，不返回隐藏节点名称、计数或可推测的占位数量。

等义 merge 只在人工审核确认等义后允许 redirect，保留旧确认/拒绝的 owner、内容身份及因果来源；目标类同时存在“确认”和“拒绝”不能任取较新时间，形成 conflict_pending，由用户解决后才改变该来源结果。非等义合并、拆分或定义扩大不自动把旧拒绝扩展到新含义，也不丢弃它：旧定义版本保留，生成迁移审核。旧人工确认同样不能被模型或 merge 静默扩大含义。

策略冲突包括旧 A 拒绝与目标 B 恢复自动的并存，即使没有确认断言，也必须保留冲突，不允许 redirect 绕过旧拒绝。M1/M4覆盖离线旧ID回传与合并后策略的因果解析。

### 11.3 多路证据与缺失输入

描述记录其生成来源、图片指纹及模型版本，同图生成的多条描述属于相关证据，不能以“多票一致”抬高置信度。人工确认与图像/描述矛盾时保留人工标签，机器部分进入待确认并展示冲突。图片不可读时可整理已有人工/元数据和生成文字候选，但任务标记 awaiting_media，不计为已完成多模态分析，不以文字单独提升 automatic；来源恢复后增量继续。无描述但图片可读可以产生图片候选，并记录 description_missing，描述到达再更新。

### 11.4 状态优先级及覆盖

主任务分区按当前输入确定：in_progress（已领取）→failed（当前尝试失败）→awaiting_media/blocked（当前缺失输入或授权/配置）→pending_update（有旧结果但版本过期）→unprocessed（无当前成功结果）→succeeded_classified 或 succeeded_unclassified（成功指纹及发现水位匹配）。每张有效引用恰属一个主状态；旧成功结果和历史错误另存，不因当前失败被销毁。原因/无描述/人脸信息是可重叠维度。

内容覆盖与任务分区正交：人工确认在媒体与定义身份有效时仍计入；新机器自动结果须匹配当前输入和质量版本；旧机器结果显示“历史结果待更新”，单列覆盖，不计作 V2 已验证自动覆盖。历史搜索兼容仍遵循已有 legacy-effective 策略，V2 浏览提供“包含历史标签”开关并标来源。默认主指标分别给出当前有效内容覆盖、历史覆盖和建议覆盖，三者重叠，不可相加。元数据和相似群组永不补足内容覆盖。

## 12. 定稿补充契约（v1.0）

### 12.1 当前证据选择和可重现指纹

描述历史全集只用于留存和审计，不直接等同于本次支持证据全集。证据选择器排除显式撤回、被人工更正替代和身份失效版本；无明确替代关系的并存版本保留冲突及来源。M0 接收选择器已经确认的证据引用，不推断哪条是“最新事实”；仅按稳定证据 ID、provenance、载荷 SHA-256 做精确去重，同 ID 不同摘要/来源报错。语义同义归并若以后引入，必须独立版本化并保留原证据映射。

指纹规范固定为 `family-classification-input-v1`：字段顺序固定；UTF-8 严格编码；字符串前缀为有符号 int32 小端字节长度，null=-1、空串=0；集合先按 ID 的 Ordinal 排序，写入 int32 数量。证据重复 ID 内容不一致拒绝，哈希统一小写64位十六进制。必填身份/策略字段禁止空白，集合必须提供（允许空集合），不自动修剪有意义的 ID 或文本。SHA-256 对规范字节计算。Windows/Linux 黄金向量及中文/分隔符/重复/乱序用例锁定规范。

### 12.2 评估范围与向量空间

服务端冻结 evaluated_label_ids、candidate_set_digest、input_acquisition_state；每个本次要求判断的标签必须返回 supported/contradicted/uncertain，允许有界分批子范围。完整性校验通过才算该子范围成功，仅更新已完整评估范围的机器断言。遗漏条目、图片无法解码、截断输出不能被解释为“全部不符合”；合法空支持集合只有在所有要求标签都有明确状态且输入完整时才可记为 analyzed_unclassified。contradicted 不写成人工拒绝。

`EmbeddingSpaceId` 包括模型制品摘要、tokenizer版本、图片预处理、维度、归一化版本；text/image 必须属于同一经过声明验证的空间。缓存以该身份和提示词集合摘要分区；未知身份、同维度但不同空间、服务切换后版本不同均拒绝比较或重新编码。现有 ClipContentClassifier 单个静态 promptTask 不能直接作为 V2 合格缓存复用。

动态聚类代表图应包括中心、边界和离群样本，LLM 可返回“仅外观相似、无可命名共同语义”，不强制命名。输入预算不足导致未见的成员不自动继承群组标签。

### 12.3 授权、同步及清理

新增独立 capability：taxonomy.read、taxonomy.propose、taxonomy.manage；标签结构管理与来源照片标签编辑权限分离，无 manage 权限只能提议，不能接受全空间结构变更。所有能力仍受空间 epoch 和来源 scope 限制。

V2 图/任务/依赖状态走独立能力协商的版本化通道；旧快照缺少字段表示“不理解”，不能清空 V2 数据。旧 publisher 只能替换其明确拥有的旧投影集合。基线包含 dependency_revision；断言早于图到达先暂存，立即请求依赖，重试到配置上限后显示 dependency_missing，不永久静默挂起。

媒体当前状态保存 desired_fingerprint/dirty_revision，工作项带 target_fingerprint；有租约时更新 desired，旧项提交如已过期不推进当前成功水位，并原子建立后继项或保留 pending obligation。唯一工作项约束不能吞掉在途的新描述。

最后授权引用清除/来源撤权后立即禁止缓存访问及新推理，取消相关项，清理该范围分析图与代表图；共享内容只在仍有授权引用时保留可访问性。模型诊断正文默认不留存，受控失败制品保留不超过7天且总量上限可配置；脱敏 receipt/tombstone 保留至所有有效消费者越过安全水位，离线过久者须重新建立基线，不能无限阻止回收。

### 12.4 条件组

V2 规范筛选使用 groups（组内 all/any，组间 AND）和全局 excluded_ids；首期最多8组、每组32个显式标签。单组 UI 可直接显示“满足全部/任一”；增加条件组支持 `人物 AND (森林 OR 草地)`，不声称支持任意嵌套布尔表达式。空组忽略，全部组为空等同全部授权照片；排除最后应用；同一标签在不同组出现不得擅自消掉，因为可能改变语义。

## 13. M0 契约落地补记

图最长路径按节点数计，根节点为第 1 层，is_a 与 browse 联合展开最多 16 层，包括停用节点；related 不参与深度或包含。维度注册为 subject、nature、place、architecture、activity、event、object、environment、document，新增维度需显式版本化契约更新。实际开发和测试状态见[实施记录](CLASSIFICATION_V2_IMPLEMENTATION_2026-10-29.md)。
