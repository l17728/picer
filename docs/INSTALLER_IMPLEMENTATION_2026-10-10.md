# 一键安装与首次配对实现记录（2026-10-10）

状态：安装契约实现与验收中，正式候选推进至 v1.2.1，尚未发布。v1.2.0 在 CI 暴露 TLS 关闭兼容问题后停止发布，标签保留用于追溯。v1.1.2 已公开的事实保持不变。本记录不把代码测试通过等同于安装器验收。

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
- 登录自启入口由安装器定义；隔离安装测试关闭自启任务，未模拟 Windows 注销再登录。最终 CI 将重新构建并验收正式安装包。

## 真实服务连接门禁

首次 CI 的 622 项单元测试中 621 项通过；失败发生在证书发现完成后的连接关闭，Windows runner 的 TLS 对端读取收到连接重置。发现逻辑现显式发送 TLS close_notify 再释放连接，保持原有“服务端读取到 0 字节应用数据”的断言，没有放宽测试。修复后本地 12 项代理设置测试通过（artifacts/installer-tests/agent-tls-shutdown.trx），等待新提交 CI 验证。

按用户要求接入真实 IndexService：三个 GitHub Secrets（PICER_LIVE_SERVICE_URL、PICER_LIVE_SERVICE_TOKEN、PICER_LIVE_SERVICE_SPKI_PIN）仅在受信任 main CI 和 Release 安装验收步骤注入，不写入源码或分发包。PR 测试不获取这些凭据。

Picer.ServiceProbe 动态加载待验收目录中的 Picer.Core.dll，调用真实 HttpIndexGateway，验证固定 DER SPKI 的 TLS、健康状态、一条照片记录的认证只读查询和错误令牌返回 401；禁止 HTTP 重定向，整体限时 60 秒。日志只输出布尔结果、程序集 SHA-256 和时间，不输出连接凭据、查询响应或照片信息。缺失凭据、证书变化或服务不可用都会使门禁失败，不静默跳过。首次本地真实连接检查已通过。

Release 对 GUI 和驻留代理分别执行“安装 → 使用安装目录通信组件连接真实服务 → 升级/卸载检查”，然后才创建草稿版本。此检查不启动扫描、描述、模型调用、代理注册或任何索引修改，也不等同于全库同步与所有业务功能验收。

当前本地 package-release 构建用于验收，最终公共 Release 在提交后由 CI 打包，不能把本地暂存资产写成已发布。安装器没有签名凭据，当前未签名；Windows 可能显示发布者未知，一键流程不绕过系统保护。支持目标为常规 Windows 10/11 x64，Windows N/Server 的 Media Foundation 等系统组件不包含在安装器内。

所有结果以实际安装及发布记录补齐，不能沿用 v1.1.2 的 610+73 或已公开状态作为 v1.2.0 新安装代码的最终 CI/Release 结论。四项既有外部质量/硬件验收仍不因安装器产出而自动完成。
