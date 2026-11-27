# 分类增量计划内存修复（2026-11-27）

## 根因

服务器只有约 1.8 GiB 可用内存。增量计划原先一次性加载整本相册的全部上下文，再把每张照片的模型输入、描述证据和完整标签定义重复保存在计划 JSON 中。Linux OOM killer 多次以退出码 137 终止 IndexService，客户端因此看到 HTTP 响应提前结束。计划请求并非凭据或驻留程序连接问题。

## 修复

- 计划计算按 200 张照片分页读取上下文，只在内存中保留当前页和精简的照片 ID、指纹、更新原因。
- 计划持久化不再重复保存每张照片的完整模型输入。
- 用户启动任务时按 100 张照片分批重建输入，并校验指纹；若源照片或索引已变化，则拒绝过期计划。
- 旧格式中已保存完整输入的计划仍可启动。

## 验收

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：0 警告、0 错误。
- 分类生命周期筛选测试：35 通过、0 失败，其中新增 205 张照片跨页计划与启动回归用例。
- 远端分类树读取成功：230 个节点、212 个可赋标签、289 条导航边。
- 真实远端计划创建成功：8,603 张照片全部进入计划，耗时约 4.5 秒；在修复前同一请求会触发 OOM 并杀掉服务进程。
- 真实远端任务已创建并运行：预算沿用当前配置的最多 100 次模型调用；最近一次观察为成功 3、失败 0、待处理 8,599、运行中 1。模型配置为本机端点且 `AllowRemote=false`。
- 只读抽查分类浏览首 200 张照片：已有 3 张获得建议标签，3 张均有多个标签，最多 13 个；确认多标签结果会被保留。
- 修复后服务器保持 active，最近内核日志没有新的 OOM 记录；主机可用内存约 730 MiB。

## 构建与部署记录

- 源码提交：`83e5053ab5595afabd6d1fec3197e055a72dbb62`，已推送至 `origin/main`。
- Linux x64 IndexService：部署包 `artifacts/indexservice-deploy/20261127T091020602Z/publish/Picer.IndexService`，SHA-256 `37717a1ac859942679c4f5819d08a6b26c9428a15169af6ae60d77954ae6c86f`。
- 生产部署：服务器 `47.103.99.229`，部署号 `20261127T091020602Z`，健康/版本检查通过；回滚备份位于 `/opt/picer-index.backup-20261127T091020602Z`。
- GUI Release：`src/Picer.App/artifacts/builds/classification-tree-counts-memory-fix-2026-11-27/Picer.App.exe`，SHA-256 `A8591586B0078B4A20E0A81579444F6B44E076A9550DEED281DED44910590E19`；配套 `Picer.App.dll` SHA-256 `EC908803DD32DB3DC52231410B48F4A19185D29F4D073B775822DD25FA49B71B`。
- GUI 本地启动验收尚未完成：检查时 Windows 处于锁屏状态；屏幕上仍有旧构建窗口。不要把旧窗口当作本次 GUI 构建。

## 模型输出失败重试补强

- 真实任务暴露模型输出格式错误：JSON 无效、证据 ID 无效。旧逻辑将这些瞬态模型输出错误直接记为永久失败。
- `ClassificationWorker` 现对 JSON 解析错误及输出/证据校验错误启用原有最多 3 次重试和退避；每次尝试仍计入配置的任务调用预算。媒体读取失败仍不盲目重试。
- 严格 Debug 整解方案构建：0 警告、0 错误；分类生命周期筛选：41 通过、0 失败。
- 重试补强生产部署：`47.103.99.229`，部署号 `20261127T093743195Z`，Linux x64 二进制 SHA-256 `c64e6627ac9483c07adcc4876e0e00aaf268dc4c03cfba5b4d382f6128061e81`；健康检查通过，服务恢复在线。
- 重启后原持久化任务已恢复；最新观察为成功 7、失败 4、待处理 8,590、运行中 2。4 个已失败项来自补强部署前，尚未对其执行定向重试。

## 保护条件

分类输出仍是建议标签；用户确认的标签不会被自动覆盖。服务器端分类仍按当前配置使用本地 MediaAgent 到本机模型端点的路由。

## 后续收尾进展（2026-11-27）

- 服务器持久化任务继续运行；最新状态：成功 8、失败 4、待处理 8,590、运行中 1（总计 8,603）。原任务调用预算保持不变，已失败的 4 项产生于重试修复部署前，需在当前批次/预算窗口后另行定向重试；没有把它们标记为已恢复。
- 最新 GUI 已用独立输出目录发布成功，避免覆盖被旧 GUI 锁定的默认 Release 目录：`src/Picer.App/artifacts/builds/classification-tree-retry-2026-11-27/publish/Picer.App.exe`，SHA-256 `15E5E5D90AAD49ADFF862622E3D9F3E786942C43EC308BFE35042F0BA5B1F189`；配套 DLL SHA-256 `A8C69EDF353412545BC483C915D5C0C7AF084EF245DB07AD5E9928A9CB6BC382`。
- GUI 尚未启动验收：此前桌面处于锁屏状态，且旧 GUI 仍占用标准 Release 输出。需要解锁桌面后再检查实际窗口并切换到上述构建；不能把旧窗口当作新版。

## 2026-11-27 Qwen3 响应格式与最新收尾记录

- 审计运行配置后更正：`MaximumCalls=10000`，上一节“100 次”记录有误；任务状态本身也报告 `maximumCalls=10000`。
- 生产日志显示模型失败集中于 `classification_json_invalid`，单次耗时约 2–4 分钟；重试只重复相同问题。将 `/no_think` 放在 system 内容后仍出现 JSON 解析失败，因此改为只对 Qwen3 模型把标记放在多模态 user 内容末尾，并在 JSON 解码前只提取一个平衡的 JSON 对象。标签 ID、完整评估、指纹和证据校验保持严格；前后思考文本不会被保存。
- 回归验证：Qwen3 标记/JSON 外层文本定向测试 4/4 通过；分类运行时筛选 41/41 通过；`dotnet build picer.sln -c Debug --no-restore --warnaserror` 为 0 警告、0 错误。
- 最终服务补丁已部署至新服务器：部署号 `20261127T142427110Z`，Linux x64 二进制 SHA-256 `08ecaa87d7710d16a628d80ac6aeaf8aa0c4d53cd4fe124d0806115208f79c59`，数据库/健康/版本验收通过，备份 `/opt/picer-index.backup-20261127T142427110Z`。
- 部署后任务状态最后观测：成功 39、失败 28、待处理 8,534、运行中 2、已用调用 125/10,000。新补丁部署后尚无足够完成样本证明模型输出失败率下降；不能标记该性能/质量问题已验收解决。
- 最新 GUI 发布路径：`src/Picer.App/artifacts/builds/classification-tree-final-2026-11-27/publish/Picer.App.exe`，SHA-256 `5C589E1F7BE2AE7B10D1EA7135B457960C7A17EB9B20240486BDCA1E5FE533D7`；`Picer.App.dll` SHA-256 `A7030B35C451A95713B3DC7219D6D51CFA01A6390FA59A9D6C17FCA2A40B73A6`。实际窗口启动和视觉验收仍待桌面解锁。

- 2026-11-27 14:33 UTC 复查：最终补丁部署后仍出现 1 条 `classification_json_invalid`（约 194 秒），说明 Qwen3 标记和 JSON 外层提取还没有证明能消除底层格式失败；最新任务状态为成功 39、失败 28、待处理 8,535、运行中 1、已用调用 126/10,000。需要继续基于响应元数据定位截断/生成格式，不能声称已彻底修复。

## 2026-11-27 截断根因修复与生产观察

- 安全诊断确认模型正文在 JSON 对象尚未闭合时到达 8,192-token 输出上限：典型响应 23–24 KiB，`startsWithJson=true`、`endsWithJson=false`，`jsonByte` 等于响应尾部；没有思考标签。根因是冗长的逐项标签结构输出被截断，而非索引、目录、凭据或源 Agent 连接。
- 已改为 `classification-indexed-v1` 紧凑返回：模型只返回 supported/uncertain/conflicting 的整数索引数组；服务器按本次 taxonomy 的稳定顺序映射回标签 ID，再检查越界、重复、完整评估和证据。单项证据由服务器绑定到 `image:current`，分类内容的用户确认流程不变。仍保留旧 sparse v1 响应解码兼容。
- 验证：索引格式映射/拒绝重复或越界等定向测试 2/2；Qwen3/包裹 JSON 定向测试 4/4；ClassificationV2RuntimeTests 41/41；严格 Debug 整解方案 0 警告、0 错误。
- 紧凑输出服务已部署到 `47.103.99.229`：部署号 `20261127T145017286Z`，SHA-256 `a1aa28d77ef886f35578740a5be82884734ae2408d2c07a71982f6f0ef7c362a`；健康/版本校验通过，回滚备份 `/opt/picer-index.backup-20261127T145017286Z`。
- 紧凑输出部署后的任务观测：成功从 39 增至 44，失败维持 30，待处理 8,527，运行中 2，调用 137/10,000。旧 30 个失败仍需待当前任务收敛后通过 `RetryFailures` 计划重试；当前没有把它们标为已恢复。
- 最新 GUI Release 路径：`src/Picer.App/artifacts/builds/classification-tree-compact-classifier-2026-11-27/publish/Picer.App.exe`，SHA-256 `8C472B615DF04CAD577165447CCE27F76F62DE99D623EC7F370E63AE4126CC80`；`Picer.App.dll` SHA-256 `91989D2407ACA65B2C14D62EF5C0087C43E3DDC5C532967208687E477CD002A6`。窗口启动和分类树视觉验收仍因 Windows 锁屏未完成。
