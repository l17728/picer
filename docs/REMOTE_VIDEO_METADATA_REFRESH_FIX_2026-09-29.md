# 远程视频详情与驻留代理恢复修复（2026-09-29）

<!-- historical-status-2026-10-09 -->
> **历史资料，2026-10-09 状态对照**：正文保留该阶段设计、测试和部署证据；其中“当前”“待办”“已部署”及验收数字按当时语境理解，不代表最新程序状态。
> 当前能力见[当前能力与验证状态](CURRENT_CAPABILITIES_2026-10-09.md)，后续结果见[最新代码修复与验证](RESIDUAL_CODE_FIXES_2026-10-09.md)。当前源码 schema 45；605 项单元用例由完整回归与补测分轮去重核验，WPF 73/73，不是一次全量 605/605，也不证明历史部署包包含新代码。
<!-- /historical-status-2026-10-09 -->

## 问题与根因

远程视频详情在目录刷新、快速切换和关键帧拉取并发时存在两类问题：

1. 视频列表刷新会清空并重建条目，但未可靠恢复当前选择及其详情；而详情任务在网络下载关键帧期间没有取消/代次检查，旧视频任务可能在用户切换后继续写入当前面板。
2. 远程关键帧物化只处理前 12 帧。人脸时间线中的帧未被下载时仍指向源机器路径，远程客户端无法读取，导致关键帧或人脸裁剪显示为空。缓存直接写最终文件名，若传输失败留下部分文件，之后会被误认为有效缓存。
3. 原图/视频驻留代理采用系统级单实例。开发输出目录曾复制一个过期的 `Picer.MediaAgent.exe`，但没有其配套 DLL；控制器优先启动这个副本，而旧代理位于 `src/Picer.MediaAgent/bin/...`，仅按首选 exe 路径清理时旧进程不会退出，新进程又因单实例锁立即退出。此前 UI 只检查 `Process.Start` 是否返回对象，因此误报“已启动”。旧实例的授权目录不含当前源目录，IndexService 因而拒绝关键帧/人脸裁剪回源。

## 实施内容

- 视频列表刷新后按 ID 重新绑定选择，重新载入该视频的关键帧、人物标注、转写、摘要。
- 远程模式启动时也立即从本地持久化镜像加载视频目录，不再等完整远程索引镜像同步（可能耗时数分钟）完成后才显示视频；后台同步完成再增量刷新并重载当前详情。
- 详情加载按当前选中视频取消旧任务，并在数据库读取、关键帧传输和 UI 分批添加之间检查取消/当前 ID；切换时立即清理旧的人脸与详情，展示加载状态。
- 远程关键帧完整物化（最多并发 4 个），不再只取前 12 帧；按时间戳分组映射，兼容重复时间戳。缓存先写临时文件，非空后原子替换，失败不留下可被误认的缓存文件；有 ID 或序号回退路径。
- MediaAgent 只从完整的代理目录/项目构建目录优先启动；清理时识别并关闭同一项目各已知输出位置的旧实例。启动后等待进程存活确认，避免将“创建了 Process 对象”误报为成功。
- 驻留代理根目录解析放到后台，并通过数据库工作队列完成；新增每阶段日志，记录源目录数、启动与结果，不记录令牌或 AI 内容。
- 远程视频目录每页批量事务写入；刷新不覆盖关键帧、人物、转写和摘要表。回归测试覆盖同一视频多次镜像刷新后这些元数据仍存在。

## 验证

- `dotnet build src/Picer.App/Picer.App.csproj -c Debug --no-restore --warnaserror`：通过，0 警告、0 错误。
- `dotnet test tests/Picer.Tests/Picer.Tests.csproj -c Debug --no-build --no-restore --filter "FullyQualifiedName~VideoIndexTests|FullyQualifiedName~NamedPersonViews|FullyQualifiedName~PersonFaces|FullyQualifiedName~People_CanBeNamedFoundAndUsedToFindImages"`：14/14 通过。
- `dotnet test tests/Picer.E2E/Picer.E2E.csproj -c Debug --no-restore --filter "FullyQualifiedName~VideoDetailsRegressionTests|FullyQualifiedName~VideoThumbnail_UsesPosterLoaderInsteadOfDecodingVideoFile"`：2/2 通过。新增回归覆盖目录刷新后所选视频的关键帧、转写和摘要仍显示。
- 启动真实 WPF 客户端后，旧 MediaAgent 被新实例替换；新进程日志确认已连接 IndexService，授权根目录为当前源目录。再次启动最新构建后也复验了该单实例重启路径。启动日志显示 UI 在代理解析期间仍及时可见。

## 当前边界

上述自动化验证证明刷新/切换和代理生命周期修复通过；真实 IndexService 当前总体镜像日志记录有关键帧、人脸、转写和摘要元数据，但单个视频是否有这些记录取决于该视频在服务端是否确实存在对应结果。没有对每个历史视频重新生成或伪造缺失元数据，也没有在本次运行中对用户指定的单个视频逐项执行 GUI 截图验收。
