# 索引服务 P1（只读服务化）实施报告

日期：2026-09-08
设计依据：[INDEX_SERVICE_CS_DESIGN_2026-09-08.md](INDEX_SERVICE_CS_DESIGN_2026-09-08.md)（已经 [多专家评审](INDEX_SERVICE_CS_DESIGN_REVIEW_2026-09-08.md) 批准）
范围：P1 只读服务化——网关抽象、HTTPS 服务骨架、契约测试、性能预算验证、客户端配置装载。App 侧完整接线（设计 §13 M1/M2/M13）留待下一增量。

## 增量 6：P2.4 App 侧接线 + 真进程冒烟（已实施）

- `AppServices`：新增 `Gateway`/`ServiceMode`；`Initialize` 按 `backend.json` 分支——local 模式照旧构建本地栈并包 `LocalGateway`，**service 模式零构建逻辑**（不创建 Db/引擎/缩略图/视觉栈，仅按 url+SPKI 或 pipeName 建网关）；`Reinitialize`/`Shutdown` 同步分支。
- `App.OnStartup`：service 模式显示"索引服务状态"窗口作为落地面（浏览界面走 gateway 属 MainViewModel 全量改造，见"遗留"）。
- 新窗口 `Views/IndexServiceWindow`（+ MainWindow"文件"菜单入口）：模式、目标端点、连接探针（3s 超时、往返耗时、schema 版本）。
- E2E `ServiceModeSmokeTests`：启动**真实服务进程** → 轮询 `service-endpoint.json` → `IndexBackendConfig` 装载（DPAPI）→ 管道 + HTTPS 双网关健康 → 登记 → 扫描 → 图像可见。4 s 完成全链路。
- 单测 `IndexBackendConfigTests` 7 项：缺省 local、local 忽略端点、service 字段解析、baseUrl 覆盖、环境变量优先、坏 JSON 回退、缺端点报错。
- 附带修复既有 main 断裂：`SuggestedPersonGroup` E2E 断言同步现行文案"高相似待确认"（经干净 HEAD worktree 验证为既有失败，与本会话无关）。

验证：构建 0/0；`Picer.Tests` 251/251；`Picer.E2E` 31/32——唯一失败为 `MainWindow_Exposes...` 首窗 4s 预算抖动：连续高负载后本机冷启动 4.4–5.2s，**干净 HEAD 同样失败**（历史通过；CI windows-latest 为准）。

## 增量 7：P2.4 收尾——service 模式浏览视图（已实施）

- `ViewModels/ServiceBrowseViewModel.cs` + `Views/ServiceBrowseWindow.xaml(.cs)`：经 `IIndexGateway` 的浏览界面（FTS 搜索、keyset"加载更多"、4 并发缩略图加载、虚拟化 WrapPanel）——不依赖 `AppServices.Db`，零 MainViewModel 改造。
- `IndexServiceWindow`：service 模式新增"浏览图库"入口（local 模式隐藏）。
- E2E `ServiceBrowseWindowTests`：桩网关 + STA 消息泵，验证经网关的列表渲染与搜索过滤。

至此设计 §2.1 目标 2"GUI 零构建逻辑"与 §6.2 service 模式可用性在 UI 层打通：service 模式下用户可经"索引服务状态 → 浏览图库"完成搜索与浏览，全部数据来自 HTTPS/管道网关。

验证：全量 E2E 32/33 通过；唯一失败为既有"首窗 4s 预算"抖动（连续负载下干净 HEAD 同样失败，见增量 6 说明）。

## 遗留（按设计顺序）

1. **P2 最后一环**：MainViewModel/PhotoGrid 读路径全量走 gateway（"客户端全量切 service 模式可浏览"），涉及 `AppServices.Db` 的 33 处消费点改造（§13.2 策略 A→B）。
2. **P3**：远端 opt-in 授权 UI、证书轮换、备份/恢复 API 化、诊断中心接入服务探针、`publish-self-contained.ps1` GUI 精简包/服务完整包双产物、SDD 与 USER_GUIDE 收尾。

## 增量 5：P2.3 来源登记、扫描任务、verified-changes（已实施）

- `src/Picer.Core/Gateway/ScanJobManager.cs`：扫描后台任务（进度/取消/完成态）。
- `src/Picer.Core/Gateway/GatewayDtos.cs`：`GatewaySourceRegistration`/`GatewayScanStart(Started)`/`ScanJobInfo`/`GatewayVerifiedChanges*` 共享 DTO。
- `IIndexGateway` 扩展 7 个方法：来源列表/登记（可读性校验）/移除（级联清理索引，原始文件永不动）、扫描启动/查询/取消、已验证回收上报——三网关（local/http/pipe）全部实现；管道协议新增 7 个 op 与服务端共享 handlers。
- 服务端 HTTP 端点：`GET/POST/DELETE /sources`、`POST /scan`、`GET /jobs/{id}`、`POST /jobs/{id}/cancel`、`POST /verified-changes`（删除物理记录 + 缩略图缓存，**保留内容层**——照片重新出现即哈希重关联，设计 §7）。
- 契约测试 +7（三传输 × 2 + 认证拒绝）：来源登记→扫描→图像可见→清除全链路、未知路径 400、回收后记录消失但内容行保留。

验证：全量 244/244 通过（13 m 45 s），0 警告 0 错误。

## 增量 4：P2.2 PipeGateway 同机管道传输（已实施）

- `src/Picer.Core/Gateway/PipeFrames.cs`：长度前缀 JSON 帧协议（上限 32 MB、精确读取、管道重试连接）。
- `src/Picer.Core/Gateway/PipeIndexGateway.cs`：命名管道客户端（持久连接 + 断线一次重连；认证帧固定 bearer token；缩略图 base64 响应）。
- `server/Picer.IndexService/PipeHost.cs`：`NamedPipeIndexServer`——单实例监听、常量时间 token 校验、与 HTTPS **共享同一 IndexApiHandlers**；`Build` 重构返回 `IndexServiceInstance`（App/Certificate/Handlers）供双传输复用。
- `service-endpoint.json` 新增 `pipeName`（随机后缀，§5.1）；`IndexBackendOptions.PipeName` 同步。
- 契约测试升级为**三传输**（local/http/pipe × 10 场景 + 3 项认证断言 = 33 用例），含管道错误令牌拒绝。

验证：全量 237/237 通过（14 m 2 s），0 警告 0 错误。

## 增量 3：P2.1 内容身份层与扫描期哈希重关联（已实施）

- `PicerDatabase` v30：新增 `image_contents` 表、`images.content_sha256` 列 + 既有数据回填 + 索引；`UpsertImage*/UpdateImage*` 维护内容链接；`UpsertContentInTransaction`/`FindRelocatableMissingRecordInTransaction` API。引用完整性由应用层保证（与 `sha256` 列同风格，避免破坏既有写入路径）。
- `IndexEngine.PersistPreparedImage`：哈希命中且旧路径确已消失 → **移动语义**（保留 record id，路径/物理元数据重建，缩略图等派生数据经 id 延续，零模型推理）；旧路径仍在 → 复制语义（新实例共享内容行）；确定性择优（同来源优先、最小 id），并记录结构化日志。
- 测试 `ContentReassociationTests`（5 项）：移动保留 id/缩略图、复制共享内容行、不同内容独立行、重索引稳定、无哈希不建链。

验证：全量 226/226 通过（16 m 9 s），0 警告 0 错误。

## 增量 2：客户端配置装载 + 测试充分化 + P1 性能门禁

| 项 | 文件 | 说明 |
|---|---|---|
| M8 | `src/Picer.Core/Gateway/IndexBackendConfig.cs` | `backend.json`（mode/baseUrl）+ `service-endpoint.json`（url/spkiPin/tokenCipher）解析；环境变量 `PICER_INDEX_SERVICE_TOKEN` 优先；解析失败回退 local；token 解密委托注入（Core 保持平台中立） |
| M8 | `src/Picer.App/AppPaths.cs` | `BackendConfigPath`/`ServiceEndpointPath` + `LoadIndexBackend()`（DPAPI 解密）；`Picer.App.csproj` 增加 ProtectedData 包 |
| — | `tests/Picer.Tests/Gateway/GatewayContractTests.cs` | 扩至 22 用例：新增 string/long 类型化游标跨 HTTP 往返、missing 排除/包含语义、越界 pageSize、错误令牌 401、缩略图 404 |
| — | `tests/Picer.Tests/Gateway/GatewayPerformanceTests.cs` | 10 万行真实 SQLite + Kestrel 实测；`DisableParallelization` 集合避免与套件并行抢 CPU；断言 p95 < 1500 ms 并打印 p50/p95/max |

实测（本机，100k 行，pageSize=200，预算见设计 §8）：

```
[perf] http search over 100k rows: p50=65.5 ms, p95=73.3 ms (预算 300 ms) ✔
[perf] local search over 100k rows: p50=64.1 ms, p95=74.4 ms (预算 300 ms) ✔
[perf] http list over 100k rows: p50=68.6 ms, p95=77.7 ms (预算 250 ms) ✔
[perf] local list over 100k rows: p50=60.5 ms, p95=67.5 ms (预算 250 ms) ✔
```

结论：HTTP 传输开销可忽略（local 与 http 差异 < 10 ms），P1 退出条件"10 万列表/搜索达 §8 预算"以 3–4 倍余量满足。

## 交付物

| 项 | 文件 | 说明 |
|---|---|---|
| N2 | `src/Picer.Core/Gateway/IIndexGateway.cs` | 网关接口 + `GatewayHealth`/`GatewayPage` DTO；分页语义复用现有 `SearchPage`/`SearchCursor` |
| — | `src/Picer.Core/Gateway/GatewayCursors.cs` | keyset 游标的类型化编解码（跨 HTTP 往返保持 DateTime/long/string 语义） |
| — | `src/Picer.Core/Gateway/LocalGateway.cs` | 进程内直调实现（走 `IndexSearchEngine.SearchPagedAsync`，与 HTTP 共享同一查询路径） |
| N3 | `src/Picer.Core/Gateway/HttpIndexGateway.cs` | HTTPS 客户端；TOFU/SPKI 固定（无 pin 时记录首次发现值）；错误响应携带服务端 detail |
| N1 | `server/Picer.IndexService/`（新项目） | `Program.cs`（数据根/端口/token 解析、endpoint 文件写入）、`IndexServiceHost.cs`（Kestrel HTTPS + bearer 常量时间校验 + 传输无关处理器）、`ServiceSecurity.cs`（自签证书与 token 的 DPAPI 落盘、`service-endpoint.json`） |
| N5 | `tests/Picer.Tests/Gateway/GatewayContractTests.cs` | 同一断言套件跑 local 与 http（真实 Kestrel + 自签证书 + pin）；外加未授权 401 断言 |

配套：`picer.sln` 注册新项目；`Picer.Tests.csproj` 增加 `Microsoft.AspNetCore.App` FrameworkReference 与服务项目引用；测试工程抑制 NETSDK1206（Windows-only 仓库无需解析 xunit 传递链的 Linux 发行版 RID）。

## 接口（P1 交付面）

```
GET  /health                                     （无需令牌）
GET  /api/gateway/v1/images?sourceId&includeMissing&pageSize&cursor
GET  /api/gateway/v1/images/{id}
GET  /api/gateway/v1/thumbs/{id}                 （JPEG，未命中 404）
POST /api/gateway/v1/search  { filters, pageSize, cursor }
```

认证：`/api/*` 全部要求 bearer token（`PICER_INDEX_SERVICE_TOKEN` 环境变量优先，否则自动生成并 DPAPI 加密落盘）。证书：首次启动生成自签证书，私钥 DPAPI 加密落盘，重启后 SPKI 稳定（TOFU 固定可用）。

## 过程中发现并修复的问题（后续阶段复用注意事项）

1. **临时密钥证书无法服务 TLS**：`CreateSelfSigned` 直接返回的证书在 Windows 上携带 ephemeral key，SChannel 拒绝——表现为一启动 HTTPS 握手即 EOF；从 `Export(Pkcs12)` 字节重新 `new X509Certificate2(...)` 后正常。二次启动（从 PFX 导入）天然无此问题。
2. **`StartAsync` + `RunAsync` 双重启动**：宿主崩溃"Server has already started"。入口统一走 `RunAsync`，endpoint 文件改由 `ApplicationStarted` 回调写入。
3. **`Results.Json(Task<T>)` 序列化 Task 本身** → 500（`ExecutionContext&` 属性异常）。端点改为 async lambda await 后返回。
4. **环境 URL 污染**：`ASPNETCORE_URLS`/`ASPNETCORE_HTTP_PORTS` 环境变量会给 Kestrel 追加监听地址，`Addresses.First()` 可能取到非 TLS 端口。已 `UseUrls()` 清空并按 `https://` 过滤。
5. **Minimal API 值类型参数必须带默认值**，否则缺参即 400。
6. **NETSDK1206**：xunit 2.5.3 的 `NETStandard.Library 1.6.1` 传递链携带版本化 RID 资产，测试工程加 AspNetCore FrameworkReference 后触发；仓库仅 Windows x64，`NoWarn NETSDK1206` 抑制（csproj 内有注释说明）。

## 验证

- `dotnet build picer.sln -c Debug --warnaserror`：0 警告 0 错误。
- 网关契约 + 性能测试 28/28 通过。
- 全量单元/集成测试 **221/221 通过**（`Category!=RuntimeComponent`，16 m 55 s，含 4 项 10 万行性能门禁）。
- 独立进程冒烟：`/health` 200、无令牌 401、带令牌 200；`service-endpoint.json` 正确写入（URL/SPKI/DPAPI token）。

## 遗留（按设计 §13 编号）

- M1/M2/M13：`AppServices` 按 `backend.json` 分支 + ViewModel 读路径走 gateway（策略 A 适配层）。
- M8：`AppPaths` 增加 `backend.json`/`service-endpoint.json` 读取。
- N4：`PipeGateway`（P2，同机默认传输）。
- M3–M7：内容身份层与重关联（P2）。
- 错误响应目前对已授权客户端返回异常类型与消息（便于诊断）；如需收紧可按环境变量关闭，不涉及隐私字段（路径/人物名不入栈顶消息之外的日志）。
