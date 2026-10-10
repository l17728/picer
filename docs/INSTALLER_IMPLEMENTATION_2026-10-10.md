# 一键安装与首次配对实现记录（2026-10-10）

状态：[v1.2.1](https://github.com/l17728/picer/releases/tag/v1.2.1) 已于 2026-10-10 03:42:52 UTC 正式公开，非草稿、非预发布，源码 0d50de424dc29288a1f3e7e5b58773facd556233。CI、安装生命周期、安装后的真实服务读取、离线构建及资产哈希均通过；本机五处程序与模型/索引服务已部署。v1.2.0 在 CI 暴露 TLS 关闭兼容问题后停止发布，标签保留用于追溯；v1.1.2 保留为历史版本。

## 本轮目标

普通 Windows 用户通过中文当前用户安装器完成 GUI 或驻留代理安装，不另找 .NET、MSVC CRT 或视频工具。GUI 包带代理，代理工具目录包含 FFmpeg、FFprobe 及许可证并自动发现。服务端保留手动部署，ModelServer 以 setup-modelserver.py 简化 venv、固定依赖及模型探测。详见[安装指南](RELEASE_GUIDE.md)。

独立代理默认创建登录自启入口；首次安装、重复安装升级、卸载与数据保留的隔离验收已通过。用户未确认连接或允许目录时不擅自连接或扫描；模型和原文件授权边界不变。

## GUI 已实现修复

原连接窗口硬编码服务 IP，并要求已有 service-endpoint.json，使新用户无法只通过界面完成首次配对。现改为用户输入地址与令牌，先无凭据 TLS 握手，再显示 SHA-256 供明确确认；固定原始 DER SPKI 验证后 DPAPI 保存并重启。健康接口公开，因此另检查受保护的只读接口验证访问令牌，验证阶段禁止重定向。

旧首版改动误把公钥 pin 当 32 字节摘要；经实际协议审查改为共享 MediaAgentSettings 的 DER 校验和无凭据发现工具。显示摘要与持久化公钥明确区分，不要求手工抄写长公钥。

App 实际采用带 ViewModel 的窗口构造，不能依赖无参窗口的 remote 标记决定本地预检。现初始化后用实际 AppServices.ServiceMode 判断：远程 GUI 不报本地组件缺失；未选模式显示连接引导，明确本地模式仍保留组件检查。

未修改真实 GUI 连接配置，未启动媒体扫描、付费分析或训练。原始文件保持只读。

## 已取得代码验证

- E2E 项目严格构建通过：零警告、零错误。
- ServiceConnectionSetupTests：12/12，失败 0、跳过 0。最终定向证据为 artifacts/installer-tests/gui-pairing-final.trx，包含 GUI 保存字段与现有代理读取入口的兼容性。
- GUI 测试覆盖：空白首次连接不写配置，HTTPS/地址/令牌/公钥格式校验，DPAPI 和两种配置加载入口，真实 TLS 发现不发送 HTTP/令牌，保存后再次通过 HttpIndexGateway 实际连接，服务/本地与配置状态四组合。
- 真实 TLS 夹具最初受 Windows Schannel 临时密钥限制；修为生成证书后通过 PFX 默认导入，未使用弱化证书协议来绕过。
- 代理设置及配对 12 项定向测试通过，包含真实 TLS 发现无 HTTP/令牌的断言；该证据不代替安装登录自启和目录访问验收。

## 本地安装与服务端准备验收

- 两个安装器均完成首次安装、重复安装升级、卸载、外部原文件及未归属文件保留；内置 FFmpeg 生成测试视频，FFprobe 验证分辨率。GUI 实际窗口响应并正常关闭，确认加载安装目录内的 .NET。证据：artifacts/deploy/installer-lifecycle-v1.2.0-retry.log。
- 驻留代理设置窗体真实 STA 测试通过：中文控件、多个目录增减、空输入拒绝保存、正常关闭。未启动真实代理或写入真实配置。证据：artifacts/installer-tests/agent-first-run-ui.trx。
- ModelServer CPU 环境完成新建 venv、依赖安装、模型加载和 worker 协议验证；只读检查不修改环境。证据：artifacts/modelserver-setup-validation/setup-verification.json。CUDA 与 Linux 环境尚未进行真实安装验证。
- 登录自启入口由安装器定义；隔离安装测试关闭自启任务，未模拟 Windows 注销再登录。最终 Release 已重新构建并完成正式安装包验收。

## 真实服务连接门禁

首次 CI 的 622 项单元测试中 621 项通过；失败发生在证书发现完成后的连接关闭，Windows runner 的 TLS 对端读取收到连接重置。发现逻辑现显式发送 TLS close_notify 再释放连接，保持原有“服务端读取到 0 字节应用数据”的断言，没有放宽测试。修复后本地 12 项代理设置测试通过（artifacts/installer-tests/agent-tls-shutdown.trx），后续完整 CI 亦通过。

后续 1a7d1e5 的 CI 38017584934 完整通过 622 项单元和 86 项 WPF 测试，并通过真实服务连接。扩展缩略图验收后，另一轮手写 TLS/HTTP 夹具在首次 GetHealth 出现 20 秒超时；测试现改用真实 Kestrel HTTPS、动态 loopback 端口，不再假设固定三个 TCP 连接，保留 20 秒期限、发现时零 HTTP、两次读取和正确令牌等全部断言。本地定向 12/12 通过，最终候选为 0d50de4。

按用户要求接入真实 IndexService：三个 GitHub Secrets（PICER_LIVE_SERVICE_URL、PICER_LIVE_SERVICE_TOKEN、PICER_LIVE_SERVICE_SPKI_PIN）仅在受信任 main CI 和 Release 安装验收步骤注入，不写入源码或分发包。PR 测试不获取这些凭据。

Picer.ServiceProbe 动态加载待验收目录中的 Picer.Core.dll，调用真实 HttpIndexGateway，验证证书发现与固定 DER SPKI、健康状态、照片索引及后续页、单张详情、视频索引、照片与视频缩略图实际解码，以及错误令牌返回 401；禁止 HTTP 重定向，整体限时三分钟。缩略图仅保存在内存中，限制压缩大小与解码像素；每页最多读取八条记录。日志只输出布尔结果、程序集 SHA-256 和时间，不输出连接凭据、查询响应或照片信息。缺失凭据、证书变化或服务不可用都会使门禁失败，不静默跳过。首次本地真实索引与两类缩略图检查已通过。该部署验收要求真实图库中存在照片、视频及可读取的缩略图，不把空图库标记为通过。

真实缩略图验收首次暴露冷源读取/传输耗时约 38.6 秒，超过代理资产请求原有 30 秒上限。该计时包含读取、编码、排队及发送，不能单独视为磁盘耗时。服务侧等待已调整为有界两分钟，仍响应调用方取消；验收客户端单请求限时 130 秒、整体三分钟。正式部署后及 Release 两个安装目录的真实照片与视频缩略图解码均通过。

新增延迟 35 秒返回资产的真实 WebSocket/HTTPS 回归；正常和延迟两项均通过（artifacts/installer-tests/cold-asset-transfer.trx），验证跨过旧 30 秒上限后仍能取得一致内容。

Release 对 GUI 和驻留代理分别执行“安装 → 使用安装目录通信组件连接真实服务 → 升级/卸载检查”，然后才创建草稿版本。此检查不启动扫描、描述、模型调用、代理注册或任何索引修改，也不等同于全库同步与所有业务功能验收。

## 本轮真实部署

本机五处安装更新并核验 159 个文件，包含新版程序、app-local CRT 和代理视频工具；程序备份为 artifacts/v1.2.1-program-backup-20261010T031421，闭库 DB/WAL/SHM 备份为 artifacts/deployment-backup-20261010T031511。真实 GUI 11600 与代理 10696 已恢复并响应，原始媒体未改动。

远端 IndexService 部署号 20261010T024817097Z，来源 07852f2567aa，二进制 SHA-256 为 e1431db407441cdf95b7582cdd1dd8cf32dd387fd3849ada5db981b1286fe92b；备份 /opt/picer-index.backup-20261010T024817097Z。校验旧完整哈希后差分重建，重建后核对新完整哈希，再执行闭库备份和升级。ModelServer 更新前暂停控制台守护，更新后恢复；备份 D:\VL-models\service-backups\modelserver-20261010T031507731。

三个数据库只读检查均为 schema 45、quick_check 正常、外键错误零，核心表数量与升级前备份一致。代理在线，FFmpeg/ASR/VL/人脸/TTS 为 ready、训练代理 configured。实际 GUI 安装目录的真实服务 probe 于 2026-10-10 03:19:09 UTC 完成证书发现、固定公钥、照片分页及详情、照片/视频缩略图解码和错误令牌拒绝，全部通过。证据位于 artifacts/deploy/live-installed-v1.2.1.log、service-restored-v1.2.1.log、local-databases-v1.2.1.json、remote-verification-v1.2.1.json。

最终提交 0d50de4 的 [CI 38019776882](https://github.com/l17728/picer/actions/runs/38019776882) 首次真实读取步骤恰遇本机代理升级停机，视频缩略图失败；恢复后现场验收通过，同一 CI 重跑成功。已下载并核对 TRX：单元 623/623、WPF 86/86，失败、跳过和超时均为零。首次失败保留为部署窗口内的可用性记录。

[Release 38019779600](https://github.com/l17728/picer/actions/runs/38019779600) 同样成功，重复通过 623/623 单元和 86/86 WPF。两个正式 EXE 完成隔离安装、重复安装升级、卸载、外部原文件与未归属文件保留，包内 FFmpeg/FFprobe 实际执行通过；GUI 窗口响应、加载包内 .NET 并正常关闭。分别从 GUI 和 Agent 安装目录加载的 Core 哈希为 916575c63c66327abfc5dc6670aa628fae1882f394b45da9a2a5ee60822b97c8，真实服务 probe 全部通过。证据位于 artifacts/ci-v1.2.1-final 和 artifacts/release-v1.2.1-evidence。

视频流式读取、边缓冲边播放、真正端到端 Range 和多连接优化已按用户要求列入[性能改造结论](PERFORMANCE.md)，尚未实施；本轮延长超时不代表这些性能能力已经交付。

公共 Release 的六个组件二进制包和两个 EXE 均由标签构建；[Offline 38020920841](https://github.com/l17728/picer/actions/runs/38020920841) 成功生成完整 GUI 与 runtime，固定组件哈希及许可证门禁通过。共 10 个软件包、15 项资产，每项均小于 2 GB；包清单与 GitHub 上传摘要逐项核对，统一 SHA256SUMS 覆盖其余 14 项资产并单独核验自身摘要。公开后再次确认非草稿、非预发布及全部摘要未变。证据为 artifacts/release-additions-v1.2.1/verified-assets.json、published-assets.json。

安装器没有签名凭据，当前未签名；Windows 可能显示发布者未知，一键流程不绕过系统保护。支持目标为常规 Windows 10/11 x64，Windows N/Server 的 Media Foundation 等系统组件不包含在安装器内。

测试、安装与发布分别采用本轮实际证据，不沿用旧版测试数量。四项既有外部质量/硬件验收仍暂停，视频流式读取与播放仍为新增待实施计划。
