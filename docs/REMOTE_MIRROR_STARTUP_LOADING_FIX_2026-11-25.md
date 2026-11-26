# 远程镜像首屏加载与人脸选择器修复（2026-11-25）

## 根因

远程浏览的权威数据源必须是 IndexService。此前远程 GUI 把 `remote-mirror.db` 当作多数页面的读源，完整同步与首屏查询又会争用 SQLite；因此远程浏览错误地依赖客户端镜像是否已同步、镜像是否可读及加载时序。镜像最多只能服务明确保留的离线/待提交功能，不能作为远程目录、搜索、视频、人物选项等已提交索引的权威读源。

2026-11-25 修正了远程主画廊、目录来源/照片/视频、相册/地理元数据、筛选选项、标签、人物选项和视频详情的读路径：这些页面改由 IndexService 直接提供已提交数据。完整镜像同步争用导致的冷启动慢仍是保留镜像功能的性能问题，不再是远程浏览首屏的正确性依赖。人物脸记录、训练历史/部分人物页内容仍有镜像依赖，明确列为未完成项。

## 修复

- 冷启动完整同步移动到目录与视频首屏加载之后开始。
- 首屏视频读取改为等待完成，不在首屏完成事件之后继续后台争用。
- 人脸模型版本与覆盖数现由服务端 `browser-metadata` 返回；人物页训练历史及人脸记录仍部分读取镜像，因此不能将人物页整体记为已迁移。
- `PeopleModelSpaceRegressionTests` 将模型历史制品下拉框验收移到人脸列表验收之前。

## 验证状态

- `Picer.App` Debug 严格构建：0 警告、0 错误（2026-11-25）。
- `Picer.Tests` 完整套件：1301/1302 通过；唯一失败为账号媒体路由测试在全量运行时等待 HTTPS 响应超时，独立重跑 1/1 通过。其余新增功能由定向测试覆盖。
- 本次修复不声称完成 Relay 的出站长连接、GUI 直连转发、多租户中继或服务端媒体加密。

## 远程目录数据源修正

- 远程模式目录树的相册成员与位置层级改为取自 IndexService 新增的轻量 `browser-metadata` 读接口，不再从 `remote-mirror.db` 构造这两类目录。
- 服务端搜索新增 `ImageIds` 条件，目录浏览点击相册/位置/场景节点后按服务端索引分页取图片记录；不逐张发请求。
- 搜索筛选标签、相机值、人物列表及人脸模型版本/覆盖数量也改由网关读端获取。远程“按人物名搜索”使用服务端人物目录 ID。
- 视频详情的人脸裁剪缩略图直接引用当前视频的远程关键帧共享缓存，不再用镜像数据库寻找服务器帧记录。
- HTTP、named-pipe 和本地 gateway 契约测试及 `ImageIds` 搜索测试已补充并通过定向验收；Release 构建、部署和 UI 验收完成，见下方记录。
- 人物页脸记录、训练历史和模型切换覆盖率仍存在镜像读取，未列为本次已解决。

## 本轮构建与验证记录

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`：0 警告、0 错误。
- 定向 `Picer.Tests`：8/8 通过，覆盖 HTTP/pipe/local 浏览元数据契约、服务端图片 ID 过滤、前台同步优先级、人脸重检及单条账号媒体授权用例。
- 定向 `Picer.E2E`：1/1 通过，确认远程视频人脸裁剪使用已物化的服务端关键帧缓存，不查镜像帧引用。
- `Picer.Tests` 全量：1301/1302 通过；唯一失败是 `AccountMediaRoutingTests.VideoPeopleAnalysisPassesScopeOnlyWithAnalysisGrant` 在 60 秒截止时间内等待 HTTPS 响应超时。单独重跑 1/1 通过，因此记录为全量中的偶发超时，不隐去该结果。
- IndexService 部署到 `47.103.99.229` 两次，第二次 deployment ID `20261125T114630214Z`，Linux x64 服务二进制 SHA-256 `50f5e0f1f2c88c6d409033e87ba95b8032d929d5d484ff6fa69497cfb38a1c70`；部署脚本返回 `Upgrade verified`，版本端点为 `https://47-103-99-229.sslip.io:8445/version`。部署脚本在升级前备份数据库/服务目录。
- 正式 GUI Release 构建 `dotnet build src/Picer.App/Picer.App.csproj -c Release --warnaserror`：0 警告、0 错误；产物 `src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe`。
- 人物页实测发现顶部 `DockPanel` 的右侧操作 `WrapPanel` 抢占全部宽度，导致模型选择器、状态摘要及分类区域显示异常；已改为全宽分行布局。修复后 Release 构建 0 警告、0 错误，正式 GUI 与 MediaAgent 已重启。最新 GUI SHA-256 `DE9452AD200D2136AF05A11E8BE8C63DBC39D4C099C1936C55E5CC14E5A0683A`。
- 修复后实际 UI 检查确认：服务地址标题正确；远程照片 8,603 张、缩略图可见；视频页来自索引服务并显示 529 项；人物页模型下拉为 InsightFace、训练历史下拉有已应用 92.2% 版本、已命名人物下拉包含 8 人；人脸列表显示 5,000/7,551 项并已载入缩略图。
- 人物脸列表、人物统计和训练历史的部分读取目前仍经过 `remote-mirror.db`；因此虽然当前 UI 项目有数据，尚未实现“所有人物浏览均以服务端索引为读源”。同时 UI 当前 `FacePartitionSummary` 的 18,053 人脸分区计数与待处理人脸列表 7,551 数量存在 13 项差异；必须把人物浏览迁到服务端分页查询并核对分区一致性，不能声称人物数据链路已彻底闭环。
- 本轮提交 `752aee2`、`f276311`、`7e71d9b`、`54cd4fb` 已于 2026-11-25 推送到 `origin/main`，本地与远端提交一致。

## 2026-11-25 人物浏览权威源补齐

本节更新上述早期验收记录：此前“人物脸列表和训练历史仍依赖镜像”的状态已由 `f562dfa` 推进。远程人物页的名字目录、人脸待处理/忽略/已命名列表、分区和摘要、训练历史均从 IndexService 同一权威索引读取；列表按视图查询，照片人脸缩略图通过既有远程媒体缓存加载。`remote-mirror.db` 不再作为远程浏览的替代读库。它仍供本地模式及部分需要本地写入/离线兼容的分析工作流使用。

- 验证：解决方案 Debug 严格构建、GUI Release 构建、IndexService Release 构建均为 0 警告/0 错误；Gateway HTTP/pipe/local 契约定向测试 78/78 通过。
- 部署：IndexService 部署 ID `20261125T122655387Z`；Linux x64 二进制 SHA-256 `92d8713823f67301190c9b899cf90e8bc2429266c59d0e0c478d3195c5409623`；部署脚本报告 `Upgrade verified`。部署前备份位于 `/opt/picer-index.backup-20261125T122655387Z`。
- GUI 正式产物 `src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe` SHA-256 `0855D9EAF7D0484F8CA2540DA96823BE7643570E2DBECC056DD0B2EC7036A4DD`；GUI 与 MediaAgent 已从 Release 路径重启。标题显示 `远程：47-103-99-229.sslip.io:8445`。
- 实际 UI 验收：远程图库 8,603 张；人物页服务端统计 28,925 张人脸，分区与列表已显示，InsightFace 模型及训练历史可见，已命名人物下拉可用；人脸裁剪缩略图在进入页面后异步下载并显示。缩略图会有网络加载延迟，界面没有把未到达的图片误当作本地数据库缺失。
- `f562dfa fix: read remote people browsing from index service` 已推送至 `origin/main`。此前同文档中的“人物列表和训练历史仍依赖镜像”是该提交前的历史状态，以本节为准。

### 尚存边界

- 人物页的某些写操作、模型切换覆盖率/本地向量补算仍使用本机工作库，因为它们执行本地分析或写入；这与远程浏览读取服务器索引是不同路径。
- 远程已命名人物页当前没有从服务器读取视频人脸时间线，显示为空；需补齐服务端视频人脸查询后才可称人物页所有媒体类型浏览完整。
- 本次增量没有完成 Relay SDD 的持久 Relay Connector、多租户端到端隔离、服务端媒体加密或 Web 管理控制台；这些仍按 SDD/E2E 清单跟踪。

## 2026-11-26 已命名人物视频人脸直读

已补齐上一节列出的远程视频人脸空白：人物详情的已确认视频人脸时间线现在由 IndexService 的 `people` 快照查询；响应同时带回视频/关键帧身份和序号。GUI 使用已有的 IndexService 关键帧下载接口及共享远程媒体缓存显示裁剪图，不再查询 `remote-mirror.db` 来找视频或关键帧引用。服务端查询只返回当前人物的已确认、仍归属于有效视频的记录，并受 5,000 项上限约束。

- 提交：`364163c fix: load named people video faces from index service`，已推送 `origin/main`。
- 验证：Debug 解决方案严格构建 0 警告/0 错误；Gateway PeopleBrowse 跨 HTTP、named-pipe、local 定向测试 3/3 通过，覆盖视频人脸、视频 ID、关键帧 ID/序号和人员归属；GUI Release 和 IndexService Release 构建 0 警告/0 错误。
- 部署：deployment ID `20261125T125625883Z`；Linux x64 IndexService SHA-256 `8622ef86b95b6dfe537a342eaf211cffa92c4afe38b5f21b4285713870f6801b`；部署脚本报告 `Upgrade verified`。升级前备份为 `/opt/picer-index.backup-20261125T125625883Z`。
- 正式 GUI 路径 `src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe`，SHA-256 `4A8CE90D23CA4BAC7F2667ED7A0C4C9561DC1B8372B6D6D2E8F0163B9D6C1624`。IndexService Release DLL SHA-256 `5EA93D1012BA0362592384C62C3D232618A90BE2F4B3D0B1E499170D5DC77259`。GUI 与 MediaAgent 已从正式 Release 路径运行，窗口标题为 `远程：47-103-99-229.sslip.io:8445`。
- 实际 GUI 已确认人物页远程索引统计、模型历史、命名人物选项及照片脸列表加载；未对命名人物下拉执行最后一次选择，因为自动化期间检测到该窗口有用户输入。视频人脸端到端服务契约由三种网关的集成测试验证，尚未据此声称对现有任一人物存在可显示的视频脸数据。
