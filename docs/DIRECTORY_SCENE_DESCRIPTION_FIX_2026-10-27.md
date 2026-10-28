# 目录批量场景描述远程模式修复

当前状态（2026-10-28）：源码修复和自动验收完成，未发布、未更新正式安装版。真实 `101APPLE` 目录端到端重试与结果保存仍由用户手工验收。

| 验证阶段 | 结果 |
| --- | --- |
| 场景总结协议修复前的全量非 RuntimeComponent 单元基线 | 1058/1058，通过；`E:/picer-acceptance-20261028/results/unit-full.trx` |
| 最终版严格构建 | 零警告、零错误；`E:/picer-acceptance-20261028-final/build.log` |
| 最终版场景、照片分析及代理路由相关回归 | 32/32，通过；含新增 4 项协议测试 |
| 最终版完整非 RuntimeComponent 界面回归 | 119/119，通过；旧 7 项视频超时未复现 |
| 实际本机模型探测 | 合成图片、合成场景描述均返回 200，未使用用户照片 |

全量单元基线运行期间发现并补修总结协议，最终版未重复运行全部单元测试，使用上述相关回归和完整界面回归验证最终改动；不将 1058 项基线冒充最终版全量结果。RuntimeComponent 不在本轮验收范围。

## 问题与原因

右键目录（用户报告为 `101APPLE`）选择“描述未分类场景”时，出现“未分类场景中的照片当前均不可读取”。目录批量入口仍使用 `File.Exists(image.Path)` 检查 GUI 本机路径，导致远程来源中有效的照片被全部过滤。此前单场景入口已修正，目录批量入口遗漏。

## 修复

- 目录批量入口与单场景入口保持一致：仅选择 Active 照片；本地模式继续检查文件存在，服务模式由既有授权媒体读取流程处理远程照片。
- 批量描述在服务模式下通过 `SaveRemoteSceneDescriptionAsync` 保存并同步镜像，沿用现有权限、身份映射与冲突处理；本地模式沿用本地保存。
- 保留模型调用确认、失败统计和现有描述复用行为，不修改原始照片，不变更数据库结构。

## 验证与交付

- `dotnet restore picer.sln` 与 Debug `--no-restore --warnaserror` 全解决方案构建通过，零警告、零错误。
- 单元测试使用 `Category!=RuntimeComponent&FullyQualifiedName~Scene` 定向运行，15/15 通过。包含代表照片选择与摘要持久化、场景分组、迁移、目录镜像顺序，以及真实 HTTPS 远程新建、编辑和并发冲突往返。
- E2E 定向运行 `SceneDescriptionEditorTests|VideoDetailsRegressionTests`：共 15 项，8 通过、7 超时失败。场景编辑窗口保存失败后保留输入并允许重试的用例通过；失败均在视频详情组（详情刷新 3 项、快速切换、人物撤回、清除、批量清除重试）。未修改视频功能，尚未诊断这些超时根因。
- 按仓库命令启动的两套全量非 RuntimeComponent 回归运行超过 12 分钟未结束，E2E 已报告视频相关超时后主动中止；不声明全量回归通过。上述定向复测仍复现视频超时。

## 目录入口回归补充（2026-10-28）

- 将单场景与目录批量入口的照片筛选抽为同一个 `SceneDescriptionImageSelector`，避免两条入口的本地/远程可读性策略再次分叉。
- 新增 selector 回归：服务模式保留 Active 远程记录，即使路径在 GUI 客户端不存在；服务模式完全不调用本地 `File.Exists` 检查。另锁定本地模式仍排除缺失文件和非 Active 记录。
- 定向验证：`dotnet test tests/Picer.E2E/Picer.E2E.csproj -c Debug --no-restore -m:1 -p:OutputPath=D:\picer\artifacts\scene-description-validation\ --filter "FullyQualifiedName~RemoteSceneAnchor|FullyQualifiedName~LocalSceneAnchor"`，2/2 通过。独立输出目录用于避开正在运行的 Picer 进程持有的默认 `Picer.App.dll`。
- 该验证覆盖照片筛选策略，未自动操作真实 101APPLE 来源、远程模型或实际目录右键菜单；真实来源操作仍需用户环境验收。全套 E2E 先前报告的视频详情超时也仍未诊断。

尚未发布或替换本机安装版本；未对用户真实目录调用模型。现有定向测试覆盖复用的场景服务与同步路径，未自动点击用户真实 `101APPLE` 目录完成模型调用。

## 收尾验收（2026-10-28）

按用户要求暂不发布。自动验收最初隔离到 `artifacts/acceptance-20261028`；磁盘不足后按用户指示迁到 E 盘，不覆盖运行中的 GUI。

首次独立并行构建因系统资源不足而失败（MSB3026/MSB4166）；当时系统可用虚拟内存不足 1 GB。关闭构建服务器后改为单进程编译，保留 `--warnaserror`。此项作为环境失败记录，不计为源码构建通过。

视频详情测试调整：将空库 schema 初始化移出原有 STA 线程 15～30 秒计时范围，记录准备耗时；界面操作及 5 秒详情等待时限不变。结果见下述定向复测与完整回归。

E 盘定向复测已完成：场景筛选、编辑窗口与视频详情共 17/17 通过（`E:/picer-acceptance-20261028/results/scene-video-targeted.trx`）。7 个视频用例的数据库准备实测 2935～4111 ms；此前超时均未复现，未修改视频产品逻辑。由于同时改善了资源环境，不能仅凭此结果认定旧超时只由建库计时导致。

完整解决方案在 E 盘严格构建通过，零警告、零错误；使用 `--artifacts-path E:/picer-acceptance-20261028 -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:IncludeRuntimeAssets=false`。`IncludeRuntimeAssets` 为现有 GUI 资产开关，不代表运行了 RuntimeComponent 测试。该版本完整非 RuntimeComponent 单元回归 1058/1058 通过，耗时约 1.16 小时；它早于后续总结协议修复，最终版另行验证如下。

完整非 RuntimeComponent 界面回归已完成：119/119 通过，0 失败、0 超时，耗时约 9.51 分钟。结果文件为 `E:/picer-acceptance-20261028/results/e2e-full.trx`，临时目录为 `E:/picer-acceptance-20261028/test-temp/e2e-full`。此前 7 个视频超时在本轮完整回归中也未复现。

用户手工验收步骤：

1. 在远程模式右键 `101APPLE` →“描述未分类场景”，应看到场景、代表照片和待分析数量确认，不再因本机没有原图而显示全部不可读。
2. 取消确认，检查没有开始模型任务，也没有新增场景描述。
3. 再次进入并确认模型调用；检查完成统计、场景标题与摘要。若来源或模型不可用，应显示真实失败，不应报告保存成功。
4. 刷新目录或重启 GUI，确认已接受的描述仍存在；如有其他客户端，可核对照片空间中的同一场景。
5. 对仍未分类的单个场景检查相同流程，确认单场景和目录批量入口行为一致。

手工验收结果待用户反馈；不将自动化测试结果替代真实目录及模型验收。

### 手工反馈：全部场景描述失败

用户确认目录入口可用，但描述全部失败。本机镜像只读查询得到本日 17 条失败记录，均为 `AnalysisStageException: Model|HTTP_503|True`，时间为北京时间 09:31:48～09:51:04。GUI 日志确认原图读取、缓冲及预处理成功；MediaAgent 日志确认请求到达本机统一模型平台 `/api/vl/chat`，上游返回 503。该路由由本机模型管理服务转发至 8000 端口的 Qwen3-VL，并非场景分组或保存接口失败。

模型启动日志显示长时间加载阶段（约 35 分钟），排查时健康检查已为 ready。使用同一路由完成两项不含用户数据的探测：最小文本请求返回 200；合成 32×32 蓝色图片请求返回 200，并返回 `{"color":"blue"}`。这证明排查时模型已恢复文本和视觉响应；真实目录重试仍由用户手工执行。

批量入口在代表照片描述全部失败时会得到空摘要，原先只累加失败数。补充完成对话框的阶段提示：有多少场景因代表照片无可用描述而未执行总结，并提示在任务中心查看照片分析错误、模型就绪后重试。该提示不把模型失败误记成保存失败，也不自动重复模型请求。

按用户新指示，后续产物改到 `E:/picer-acceptance-20261028`，测试临时数据也使用 E 盘。D 盘本轮新生成的隔离构建二进制已清理，原模型、原照片及运行中的 GUI 未删除或替换。

### 继续排查：场景总结协议不匹配

照片分析后的实际总结路径另有独立缺陷：`ServiceSceneGroupSummarizer` 向旧 `/api/service/v1/vl` 发送 `operation=scene_summary` 和描述列表，MediaAgent 的 `vl.analyze` 处理器却先要求 `imageBase64` 或图片路径，会拒绝纯文本描述。旧入口还使用 `service-vl` 占位模型，未读取功能实际配置。此前的场景分组测试使用模拟 summarizer，不能证明这段真实请求协议可用。

修复为使用同一账户空间的 `/api/ai/v1/chat/stream` 中转通道，以标准消息发送按顺序排列的代表照片描述；模型取自“未分类场景描述”的配置。保留客户端设备认证，兼容非流式 JSON 和 SSE，并拒绝流中出现的错误，即使此前已收到看似完整的摘要。该变更不自动重试模型请求，不直接连接云端服务。

新增 `SceneSummarizerTransportTests` 覆盖上述请求、认证、作用域与失败契约。此修复发生在前述完整回归启动之后，最终构建另存 `E:/picer-acceptance-20261028-final`，验收结果需要与前一版区分记录。

最终版严格构建通过（零警告、零错误）。`Category!=RuntimeComponent&(FullyQualifiedName~Scene|FullyQualifiedName~CloudVision|FullyQualifiedName~AgentConnectionRegistry)` 定向回归 32/32 通过，含新增 4 项协议用例；结果为 `E:/picer-acceptance-20261028-final/results/scene-transport-final.trx`。真实本机模型另用两条合成描述验证，返回 HTTP 200，有有效场景名称、摘要和 5 个搜索词，不含用户照片数据。

手工验收构建启动入口为 `E:/picer-acceptance-20261028-final/Start-Picer-GUI.cmd`，使用本机已安装的 x64 .NET 启动，继续读取现有用户配置。它不是安装包，不更换正式安装版。

最终版完整非 RuntimeComponent 界面回归 119/119 通过，结果为 `E:/picer-acceptance-20261028-final/results/e2e-final.trx`。这次完整界面回归与上述 32 项定向测试均在场景总结中转修复后重新构建的最终版本上执行。
