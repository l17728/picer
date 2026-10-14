# 账号空间媒体链路与健康检查修复（2026-10-14）

## 定位结果

账号空间代理曾遗漏转发 `X-Picer-Media-Streaming`，因此客户端收到的 HEAD 元数据把可用源端流标成不支持。视频缩略图可以从已有缓存读取，不能证明原视频可流式读取。补充严格验收后又定位到 `POST /api/gateway/v1/media-lease` 未配置 `media.read` 权限，Range 请求完成后的租约释放返回 403。

生产健康检查也存在独立配置错误：脚本用 `https://127.0.0.1:8445/health` 请求账号模式服务，而服务强制校验公开 Origin/Host，探测因此失败，systemd 定时器会周期性重启服务。

## 修复

- 账号空间代理转发 `X-Picer-Media-Streaming` 和 `If-Match`，让客户端正确识别流媒体能力并把实体标签校验传给源端。
- 仅为 `POST /api/gateway/v1/media-lease` 增加 `media.read` 权限。
- 集成测试覆盖远程 HEAD 能力标记、带 If-Match 的 Range 下载、租约释放，以及请求方伪造其他驻留程序 ID 时仍按已绑定来源路由。
- CI 真实服务探测启用 `PICER_REQUIRE_THUMBNAILS=1` 和 `PICER_REQUIRE_STREAMING=1`，必须实际解码照片/视频缩略图并完成视频首尾分块读取。
- systemd 健康探测从 `picer-index.service` 读取公开 Origin，使用正确 Host/SNI 并通过 loopback 连接；不匹配 Origin 时不会再误报服务不可用。

## 验证与部署

- `AccountMediaRoutingTests.MediaUsesBoundSourceDespiteCallerAgentOverrideAndRevocationClosesSocket` 针对性测试通过，覆盖 HEAD、Range、If-Match、租约释放和设备撤销。
- CI `38579180174` 曾在原有非严格探测中通过完整构建、单测和桌面 E2E，但实际输出显示 `mediaRangesVerified=false`；该结果不作为媒体读取验收。
- CI `38590850055` 已全部通过：严格真实服务探测、完整构建、单元/集成测试及桌面 E2E 均成功。前一轮 `38587932389` 曾出现一次既有流连接复用测试的并发计数波动（期望 2、实际 1）；后续完整 CI 重跑通过，该波动未复现。真实探测输出明确为 `photoThumbnailDecoded=true`、`videoThumbnailDecoded=true`、`mediaRangesVerified=true`。
- 生产 IndexService 已通过 `tools/deploy-indexservice-linux.ps1` 原子升级到 `6338130`：deploymentId `20261014T151656330Z`，SHA-256 `57d042b63acb741a61ab78de0dc35b48620664982c2f62da305b1f047d58710d`。完整数据库备份位于 `/opt/picer-index.backup-20261014T151656330Z`；公网 `/version` 的提交、部署编号和哈希与发布文件一致。
- 健康探测修复 `89b0d4e` 已安装到生产主机；连续手工探测成功，IndexService MainPID 保持 `1143362`，定时器下一周期后也未再重启服务。

## 验收边界

CI 使用现有 `legacy-owner` 账户完成真实公网登录、临时 GUI 设备配对/授权/撤销，并读取该空间绑定的本地 MediaAgent 媒体。公开注册表单的完整首次注册流程由隔离 HTTPS 端到端测试覆盖；本次没有创建额外的生产注册账号。新用户批量注册生命周期不由 CI 自动生成生产账号。
