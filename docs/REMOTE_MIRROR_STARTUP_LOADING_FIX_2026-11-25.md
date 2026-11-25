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
- 正式 GUI Release 构建 `dotnet build src/Picer.App/Picer.App.csproj -c Release --warnaserror`：0 警告、0 错误；产物 `src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe`，当前 SHA-256 `2A0E3563F2F229BFFB8B61BEC9E3D7AD7B29DB31B2617CC315F5DA90BFCB0388`。已从该正式路径启动 GUI 与 MediaAgent。
- 2026-11-25 实际 UI 检查：窗口标题为 `Picer - 图片管理（远程：47-103-99-229.sslip.io:8445）`；目录树、缩略图和 8,603 张远程图库状态可见；视频页显示“共 529 个视频（来自索引服务）”并列出视频；人物页模型/历史选择器有值，统计文本显示 28,925 张人脸。人物页分类内容仍在加载/显示不全且布局挤压，须作为后续专项修复，不能宣称人物页全部验收完成。
- 本轮本地提交：`752aee2 fix: read remote browsing data from index service`、`f276311 fix: load remote face model options from index service`。推送因网络连接 GitHub 443 超时/重置尚未成功，需继续按 `docs/ENGINEERING_MEMORY.md` 的直连与备用路由重试；仓库提交目前领先 `origin/main` 两个提交。
