# 跨机 IndexService 实机验收（2026-09-20）

## 部署

- 服务端：`124.156.193.122`（Ubuntu 5.15，x64）。
- 服务：Linux-x64 自包含 `Picer.IndexService`，systemd 单元 `picer-index.service`。
- 监听：`https://0.0.0.0:8445`，数据根目录 `/var/lib/picer-index`。
- 客户端：本机 Picer GUI，配置为 service 模式，远端地址 `https://124.156.193.122:8445`。
- 认证：bearer token；客户端固定远端证书 SPKI，不关闭应用层 pin 校验。

## 验收结果

| 项目 | 结果 |
|---|---|
| 远端 systemd 服务 | 通过，active/running |
| HTTPS `/health` | 通过，HTTP 200，schema v37 |
| 带正确令牌读取 sources | 通过，HTTP 200 |
| 带正确令牌读取 images/search | 通过，HTTP 200，空库返回稳定 |
| 错误令牌 | 通过，HTTP 401 |
| SPKI 计算与固定值比对 | 通过 |
| 停止服务后的断线表现 | 通过，客户端连接失败而非回退本地索引 |
| 服务重启后恢复 | 通过，重新返回 HTTP 200，schema v37 |
| 本地 GUI | 已启动并进入“索引服务状态”窗口，进程响应正常 |

另外使用客户端真实 `HttpIndexGateway` 代码路径完成 pin 校验、health、sources 和 search 调用，结果为 `health=ok; schema=37; sources=0; searchTotal=0`。

## 兼容性修复

IndexService 原先仅使用 Windows DPAPI 保存证书和令牌，在 Ubuntu 上会抛出 `PlatformNotSupportedException` 并进入重启循环。现已增加跨平台安全降级：Linux 使用受限数据目录中的明文文件权限保护，Windows 继续使用 DPAPI；服务令牌仍不写入日志。服务默认仍只监听 loopback，只有设置 `PICER_INDEX_SERVICE_BIND` 才允许跨机绑定。

## 限制

本次远端索引库是新建空库，因此远程列表/搜索返回空结果是预期的。要展示实际照片，需要在远端注册其可访问的照片目录并执行扫描；客户端本地文件路径不会自动成为远端机器可见路径。
