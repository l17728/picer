# 用户标签与"我的相册"同步实现报告(2026-11-20)

## 背景与问题定位

用户在"标签与相册"页创建的用户标签不会出现在目录浏览的"我的相册"下。定位结论:

- "我的相册"目录树(`MainViewModel.LoadDirectoryTreeAsync`)只从 `albums`/`album_images` 表构建;
- 手动加标签(`SemanticViewModel.AddTagAsync`)只写 `tags`/`image_tags`,两套字典之间不存在同步通道;
- "按相机创建相册"流程(`SemanticEngine.AutoCreateCameraAlbums`)是唯一的双写路径(同时建 `相机: X` 相册和品牌小写标签),造成"相机标签曾同步"的假象;
- 添加标签后的目录树刷新链路(`_changed` → `LoadDirectoryTreeAsync`)本身是通的,缺的只是数据层桥接。

## 产品决策(已与用户确认)

1. 用户标签即相册:创建标签时同步创建同名相册,标签图片成员镜像进相册;
2. 删除联动:删除标签时一并删除其关联的同名相册(仅限标记相册,见下);
3. 存量回填:为已有旧标签批量创建同名相册并纳入已打标图片。

## 实现

### `src/Picer.Core/Search/SemanticEngine.cs`(新增 Tag Albums 区域)

- `TagAlbumDescription = "用户标签相册（自动同步）"`:标记相册描述。只有携带该标记的相册才随标签删除;手动相册、相机相册即便同名也不受影响。
- `FindTag` / `FindAlbumByName`:按名称精确查找(与 SQLite TEXT 默认 BINARY 比较一致)。
- `AddTagWithAlbum(db, name, imageIds)`:创建或复用同名标签与相册,并对每张图片同时执行 `AssignTag` + `AddToAlbum`(继承既有的 `tag-link`/`album-link` 元数据编辑记录,远端同步与目录退休簿记自动生效)。已存在同名手动相册时复用之,仅补充成员,不改其描述。
- `DeleteTagWithLinkedAlbum(db, tagId)`:删除标签;若同名相册携带标记描述则一并删除,返回是否删除了相册。
- `BackfillTagAlbums(db)`:为每个存量标签确保同名相册存在并镜像 `image_tags` 成员(`INSERT OR IGNORE` 语义,经 `AddToAlbum` 走元数据编辑);相机品牌标签(名字等于 `相机: X` 品牌的小写下划线变形)与既有相机相册和解,不产生重复相册;返回新建相册数,重复运行为增量空操作。

### `src/Picer.App/ViewModels/SemanticViewModel.cs`

- `AddTagAsync` → `AddTagWithAlbum`;同名标签/相册已存在时复用而非报"标签已存在";状态文本说明已同步到"我的相册"。
- `AcceptSuggestionAsync` → 同样走 `AddTagWithAlbum`(接受建议也是创建用户标签)。
- `DeleteSelectedTagAsync` → `DeleteTagWithLinkedAlbum`;确认对话框在存在关联标记相册时明示相册将一并删除;删除后刷新相册列表。

### `src/Picer.App/ViewModels/MainViewModel.cs`

- `InitializeAsync` 在首次 `LoadDirectoryTreeAsync` 之前执行 `BackfillTagAlbums`(本地/服务模式均执行,与标签页写入路径一致),新建数记入日志。

## 验证

- 新增 `tests/Picer.Tests/TagAlbumSyncTests.cs`(4 项):双写与成员镜像、同名手动相册复用不覆盖描述、标记相册随标签删除而手动同名相册保留、回填含相机标签和解且幂等。
- 扩展 `tests/Picer.E2E` `DirectoryBrowser_SourceAlbumsAndTimelineRootsAlwaysResolvePhotos`:种子加入用户标签"海滨之行",断言启动回填后"我的相册"子节点为 `["海滨之行", "回归相册"]`(相册按创建时间倒序),且选中标签相册节点能解析出照片。
- 严格构建(`--warnaserror`)0 警告;上述针对性测试全部通过;完整单元套件经 `tools/run-picer-tests.ps1` 运行:1239 项中 1237 通过,2 项失败(`MediaStreamingTests.SourceThumbnail_TransfersOnlyResizedPixels_AndCoalescesRequests`、`MediaStreamingTests.SaturatedOriginalReaders_LeavePreviewChannelAvailable`)。归因:已用 `git stash` 在无本改动的干净基线上复验,同样 2/2 失败(自托管 Kestrel 媒体流在 HttpClient 30 秒超时挂起)。归因(2026-11-23 修订):非本机环境问题,而是 main 上的既有测试漂移——`01bdd40`(2026-11-18)新增 `media.preview` 持久预览协议但未同步更新测试替身 `TestAgent`,缩略图请求在 `RequestPreviewAsync` 等待 2 分钟直至客户端超时;与本改动无关。此前"CI 为绿"的表述未经核实,应予撤回。
- 完整 E2E 套件:171 项中 167 通过,4 项失败(`LocalAccountSynchronizationTests.LocalModePublishesAndDownloadsThroughItsOwnPairedAccount`×2、`ClassificationV2UiTests.IndependentGuiExposesTreeTogglePreviewAndScanWithoutModelCalls`、`DesktopSmokeTests.MainWindow_ExposesCoreNavigationAndSearchToUiAutomation`)。同样经干净基线复验 4/4 失败。归因(2026-11-23 修订):非本机环境问题,而是 main 上的既有测试漂移——`0e486e7`(2026-11-17)加入的 E2E 断言期待从未在应用中存在的菜单项"语义索引管理与诊断"(实际为"中文语义检索")和窗口 `ClassificationV2Window`(实际为 `ClassificationBrowserWindow`);`afefd2d`(2026-11-19)在本地索引模式关闭 `ContinuousSync` 后,`CaptureSpaceNotificationBaselineAsync` 返回 null,而 `LocalAccountSynchronizationTests` 未同步更新;与本改动无关。


## 边界与已知限制

- 相册成员仅在标签创建/接受建议/回填时镜像;其他路径(如视频元数据窗口、远端同步导入的标签)造成的成员变化要等下次启动回填才会对齐。
- 远端镜像经同步导入的标签同样在下一次启动回填时补建相册;回填经 `AddToAlbum` 记录 `album-link` 编辑,会随既有同步机制外发。
- 删除联动只认标记描述;同名手动相册会存活(有意为之,避免误删用户手工相册)。
- 未修改 `AddTag`/`DeleteTag` 原语,既有目录退休/同步测试语义不变。

## 正式构建记录

- Commit:`980ee34`(feat: sync user tags to same-named albums under directory browsing)
- 构建:`dotnet build src/Picer.App/Picer.App.csproj -c Release --warnaserror`,0 警告 0 错误(Release 自包含 win-x64;注意 `RuntimeIdentifier` 仅在 Release 配置生效,还原/构建需带 `-c Release`)
- 产物:`src/Picer.App/bin/Release/net8.0-windows/win-x64/Picer.App.exe`
- SHA-256:`a74adb8090348f384b6e4e72f6879def328acdb3225cd5357691daebc670f38c`
- 构建时间:2026-11-20 18:39
