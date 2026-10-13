# Picer 照片与视频管理器

Picer 提供照片和视频索引、目录描述、人物管理、文字检索与 AI 问答、移动后索引修复，以及远程预览和视频播放。Windows x64 GUI 与驻留代理可分别部署；IndexService 和 ModelServer 提供 Windows/Linux x64 服务包。扫描和分析保持原始媒体只读，文件回收与索引清除分别由用户明确授权。

## 当前版本

[v1.5.1](https://github.com/l17728/picer/releases/tag/v1.5.1) 已正式发布，数据库 schema 46。包含账号/空间/设备授权实现、流式目录描述与可靠性改进，以及 GUI、Agent、IndexService、ModelServer 的组件包和离线安装包。现有生产 IndexService 已升级到 schema 46，但继续以旧令牌协议运行；账号门户/多租户模式尚未在生产启用，切换门禁见[部署记录](docs/DEPLOYMENT_V1_5_1_2026-10-14.md)。人脸质量、人工标注视频转录、AEC3 房间体验及低配置设备四项外部验收仍暂停。仓库为私有，Release 下载需要访问权限。

普通 Windows 用户下载 `Picer-v1.5.1-win-x64-setup.exe`，大小 **326,183,841 字节（约 326 MB）**。安装器包含 GUI、驻留程序、自包含 .NET/Kestrel 和媒体工具；纯远程客户端不需要 Python、CUDA 或模型包。新装 GUI 默认不启动本机驻留代理；保存原始媒体的电脑按授权配置运行代理。新源端能力需配套升级 Agent 和 IndexService。完整安装与验证见[安装指南](docs/RELEASE_GUIDE.md)、[离线安装说明](docs/OFFLINE_INSTALL.md)和[v1.5.1 部署记录](docs/DEPLOYMENT_V1_5_1_2026-10-14.md)。

## 从哪里开始

| 需要 | 文档 |
| --- | --- |
| 安装、首次连接、代理是否启动 | [安装指南](docs/RELEASE_GUIDE.md) |
| 描述目录、搜索、播放、移动修复与清除 | [用户指南](docs/USER_GUIDE.md) |
| 当前完成项、验证边界与暂停事项 | [能力状态](docs/CURRENT_CAPABILITIES_2026-10-09.md)、[开发计划](docs/DEVELOPMENT_PLAN.md) |
| 服务部署、依赖、认证和恢复 | [服务说明](server/README.md) |
| 架构、数据身份、缓存与安全约束 | [SDD](docs/SDD.md) |
| 发布测试与实际性能 | [媒体实施记录](docs/MEDIA_STREAMING_IMPLEMENTATION_2026-10-10.md)、[性能说明](docs/PERFORMANCE.md) |
| 全部文档及历史报告 | [文档导航](docs/README.md)、[历史索引](docs/ARCHIVE_INDEX.md) |

## 开发验证

在 Windows x64、.NET 8 SDK 环境，从仓库根目录执行：

```powershell
dotnet restore picer.sln
dotnet build picer.sln -c Debug --no-restore --warnaserror
dotnet test tests/Picer.Tests/Picer.Tests.csproj -c Debug --no-build --no-restore --filter "Category!=RuntimeComponent"
dotnet test tests/Picer.E2E/Picer.E2E.csproj -c Debug --no-build --no-restore --filter "Category!=RuntimeComponent"
```

桌面测试驱动真实 WPF 和隔离的回收站操作，须在可交互 Windows 环境执行。RuntimeComponent 依赖固定哈希的离线资产，不包含在上述集合内；新克隆不会自带这些资源。贡献约定及数据库迁移保护见 [AGENTS.md](AGENTS.md)。
