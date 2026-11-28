# Relay protocol v1 cryptographic frame primitives (2026-11-26)

账号服务支持在隔离验收中转发握手及加密数据帧，本机服务已处理部分索引只读请求。生产配置仍仅启用控制通道，正式 GUI 的模式选择尚待接入。

## Key agreement

Each peer creates an ephemeral P-256 ECDH key share bound to the same `spaceId`, `sessionId`, initiator device ID, responder device ID, sender device ID, nonce, creation time, and expiry. The complete share is signed with the sender's existing P-256 device identity key. The receiver must supply the expected peer signing public key from an authenticated account/device registry; a key carried only inside the relay frame is never trusted.

Both peers derive the same 64 bytes using ECDH followed by HKDF-SHA256. The transcript salt includes both ordered public key shares, their nonces and signatures, and all session/tenant/device bindings. HKDF output is split into independent initiator-to-responder and responder-to-initiator AES-256-GCM keys. The Relay can forward the signed public shares but cannot derive these keys.

## Data frames

The AES-GCM associated data binds version, session, space, sender, recipient, sequence, request ID, idempotency key, kind, and expiry. The operation name, API path, query, metadata, and payload belong inside the encrypted payload. Frames are limited to 2 MiB plaintext; sequence numbers are per session and direction. The receiver checks the peer/session/tenant binding and an expiry no more than five minutes ahead before decrypting, then updates a bounded 4096-sequence sliding replay window only after successful authentication. The window allows concurrent streams to arrive out of order while rejecting duplicates and sufficiently old frames.

## Current code and tests

- Primitive implementation: `src/Picer.Core/Identity/RelaySessionCrypto.cs`.
- `src/Picer.Core/Identity/RelayRequestMultiplexer.cs` correlates concurrent requests by request ID and idempotency key, bounds in-flight work (32 by default, max 256), handles timeout/cancellation and releases pending capacity. Request timeout is capped at five minutes to match frame expiry. A late response for an expired/cancelled request is discarded.
- `src/Picer.Core/Identity/RelayWireProtocol.cs` defines bounded binary WebSocket envelopes for signed key exchanges and encrypted frames; it accepts fragmented binary messages up to 3 MiB and serializes concurrent sends on one socket.
- Tests cover signed two-party agreement, bidirectional encryption, frame binding, tamper detection, invalid peer identity, expiry, replay, out-of-order sequence acceptance, concurrent response correlation, capacity bounds, and timeout cleanup.
- Debug full solution build: 0 warnings, 0 errors. Relay credential/connector and protocol tests: 11/11 passed.
- Release artifact path: `server/Picer.IndexService/bin/Release/net8.0/Picer.IndexService.dll`; SHA-256 `E4FE9A7D1F25E8EB292D6E086C3A46CF952B25E42BA742C093A1F40BEEF7249B`.
- Idempotency key is cryptographically bound to a request frame, but no durable request ledger or exactly-once write execution exists yet. This must be implemented at the local IndexService transaction boundary before write operations are routed.
- The multiplexer accepts single-frame and bounded multi-frame responses. GUI integration, additional media and write operations, durable replay state, throughput/backpressure acceptance across tenants, and complete tenant revocation acceptance remain outstanding.

Do not enable data forwarding until those pieces are integrated and the E2E cases for RELAY-03, 09, 14, 17, 18, 20, 21, 33, and 35 pass. The protocol layer alone is not full end-to-end Relay acceptance.

### 2026-11-28 并发读取和取消

本机连接器在处理查询期间持续接收数据帧，每个请求有独立 SQLite 连接和取消令牌。已认证的 cancel 帧需要匹配执行中的请求 ID 和幂等键；请求过期或连接关闭也会取消执行。只读处理器将令牌传给支持取消的搜索和目录查询；同步接口在读取前后检查取消。会话最多同时执行 32 个请求。正在写出的 WebSocket 帧使用连接级令牌保证帧完整，关闭客户端会等待活动调用退出再释放加密上下文。

隔离账号服务的两个独立 GUI 客户端凭据同时向一个本机连接器发出六个搜索请求，分别请求一条和两条数据，各自返回与本机查询一致的有序记录。在请求执行器的可控慢任务测试中，显式取消或到期都中断了目标任务，另一个请求继续完成；错误的幂等键无法取消目标。Release 定向测试 17 项通过，Debug 整解方案严格构建通过。这些测试覆盖查询并发，RELAY-14 还需要包含媒体和描述操作的验收。

Release 文件为 `D:\picer\src\Picer.Core\bin\Release\net8.0\Picer.Core.dll`（SHA-256 `D7D948E0F78145C2DEB8464624D1EE67376CD904D12D6C6DCEED2D6D5BC81BBB`）和 `D:\picer\server\Picer.IndexService\bin\Release\net8.0\Picer.IndexService.dll`（SHA-256 `A998090728CD9BDCDF6E27EACE2FA795FA84175071664116E5B9DE50CD5F98A7`）。

### 2026-11-28 本机索引查询与自动接入

账号服务在控制通道的心跳确认中告知连接器有待接邀请。连接器随后建立出站数据连接、验证 GUI 的设备身份，并在每次读取前核对其空间授权。查询复用本机 IndexService 的 `IndexApiHandlers`。客户端通过 `RelayIndexHttpHandler.ConnectAsync` 取得 `HttpIndexGateway`，后续的健康、来源、目录、相册、标签、浏览元数据、照片搜索和视频搜索共用该会话。此版本支持索引读取；媒体访问、写操作和执行中取消还需独立验收。

Release 测试在独立目录创建本机 SQLite 图库与账号服务，使用已配对的连接器、真实 HTTPS 和 WebSocket 查询。两条照片索引通过两次分页返回，结果与本机直接查询的顺序和路径相同。来源路径、目录汇总、健康响应也与本机结果比较；测试图库的视频页为空。定向测试 15 项通过。正式 GUI 窗口尚未参与这次测试。

Release 构建路径为 `D:\picer\src\Picer.Core\bin\Release\net8.0\Picer.Core.dll`（SHA-256 `ED478F0C0B4D425AE21F605FF79E1735BFF07E0BAD9BB00B48B4A03525EF2EAD`）及 `D:\picer\server\Picer.IndexService\bin\Release\net8.0\Picer.IndexService.dll`（SHA-256 `D91D57AE6A61685FAF2E0984BD2800AD8250E3C6C15CE59C76B2FBE2421F56FC`）。

### 2026-11-28 账号服务双向密文转发

GUI 以目标设备和会话 ID 发起出站 WebSocket。本机 IndexService 可以通过邀请接口发现待连接的 GUI，用自己的设备凭据加入同一会话。两个端点在同一空间内分别通过身份校验，配对等待上限为 45 秒。控制连接继续用于心跳，数据连接可以承载多次请求。账号服务核对每个方向的空间、会话及收发设备，首帧必须是签名密钥交换，后续只能转发密文帧。任一端退出都会取消配对端的接收；授权撤销沿用账号服务的撤销观察机制。

隔离 HTTPS 测试使用真实 WebSocket 发送双向签名交换、一条加密请求及其加密响应，验证撤销连接。测试正文为固定样例，真实索引查询还需接入本机处理器。Release 构建位于 `D:\picer\server\Picer.IndexService\bin\Release\net8.0\Picer.IndexService.dll`（SHA-256 `7536C295ECC403D94F3B18E26F3D3B63ED5CACE5BC4C9DA19DC6B7A831D85A08`）及 `D:\picer\src\Picer.Core\bin\Release\net8.0\Picer.Core.dll`（SHA-256 `0E000A1E979992BEBD16DA898874E2C1C7958DA1F9CE7132F8E780D86080D456`）。

### 2026-11-28 在线设备发现与握手身份验证

空间内的 GUI 可调用 `GET api/index/v1/peers` 枚举已连接且授权有效的本机 IndexService。双方按目标设备 ID 调用 `GET api/index/v1/peers/{deviceId}` 查询对方已注册的签名公钥。账号服务校验设备角色、空间成员关系、授权和会话有效期，响应禁用缓存。GUI 与本机 IndexService 分别用自己的设备密钥签署临时密钥，再用本次查询的公钥验证对方签名。

使用隔离的真实 HTTPS 账号服务和已配对客户端验证了在线发现、双向查询、密钥协商、密文解密、跨空间隔离、撤销后的目录移除与控制连接断开。Release 定向测试 15 项通过。此测试在客户端间传递握手消息和密文，账号服务尚未承载这些数据帧。

Release 构建文件为 `D:\picer\server\Picer.IndexService\bin\Release\net8.0\Picer.IndexService.dll`（SHA-256 `23F57EFD953AF8FB64EFDC7127DBD29B21E83F6C55A626EEDE8C1A316AD1B25E`）和 `D:\picer\src\Picer.Core\bin\Release\net8.0\Picer.Core.dll`（SHA-256 `72FEB0EECFE6B6A308EAEAEDA967288A89966C6AC99ACB9BAD5DC7BE33C6BF65`）。

2026-11-28 验证了请求排队超时。并发槽位用尽时，等待时间计入同一个请求期限。排队超时不会发送请求帧或取消帧，已占用槽位的请求可以继续执行。相关协议测试 5 项通过。

### 2026-11-28 加密多帧响应

协议层支持在同一请求内返回最多 8 MiB。正文不超过 2 MiB 时沿用单个 response 帧；更大的正文全部使用 chunk 帧。每块的加密正文以两个大端序 32 位整数起始，依次表示总长度和字节偏移。除末块外，每块数据长度均为 2 MiB 减 8 字节。发送端逐帧等待传输，接收端核对请求、幂等键、总长度和连续偏移，收齐才交付。单个复用器为未收齐响应预留的空间合计上限为 32 MiB。该协议适用于有界响应，任意长度的媒体流仍需独立验收。

Release 定向测试涵盖空响应、单帧临界值、超过单帧一个字节、8 MiB 完整传输、超过上限及缺首块。帧经过加密、线上格式编解码、解密与组装。正式构建路径为 `D:\picer\src\Picer.Core\bin\Release\net8.0\Picer.Core.dll`，本阶段 SHA-256 为 `003D5D6057C13C6044746E3C12BA0DD5C3C66A19B61A508999F24C9B36ABC4E3`。

### 2026-11-28 Relay 照片缩略图及授权预览

账号目录返回对端当前登记的权限，连接器对照片缩略图核对 thumb.read，对 1600 像素预览核对 media.read。缩略图复用本机 IndexService 的缩略图文件与解密读取路径；需要来源端生成时，沿用普通服务端点的 MediaAgent 预览接口和缓存。媒体状态码与 JPEG 在加密通道内传输。客户端媒体缓存作用域包含服务空间、目标 IndexService 与 GUI 设备。

隔离图库测试使用 640×320 的 JPEG，首次经 Relay 取得 256×128 缩略图。独占原文件期间，第二个客户端仍读到相同 JPEG，磁盘上的加密缓存字节保持一致。无媒体读取权限的设备请求大预览得到 403，未触发来源回调；授权设备得到可解码的大预览。此项用文件读取回调代替来源设备；真实驻留程序的原图范围读取以及正式 GUI 双击仍待验收。

正式工程所在的 D 盘在复验期间从 Windows 掉线。九个修改文件在掉线前保存到了 C 盘，并覆盖到从主线提交 aba6ef0 检出的独立验证工作区。该工作区的 19 项 Release 定向测试全部通过。验证构建路径为 C:\Users\a\AppData\Local\Picer\engineering-backups\relay-photo-20261128\verification\src\Picer.Core\bin\Release\net8.0\Picer.Core.dll（SHA-256 08E04429DC15A0B985A5ABD6120B193EB0CAE000AAF2BECC70CA94D4608D80B9），同检出的 server\Picer.IndexService\bin\Release\net8.0\Picer.IndexService.dll SHA-256 为 466EDAE38435D82A83A4C5C568863E83CF647D2968CF3C53EA64881E793B8DD6。正式 GUI 的启动路径保持在 D 盘。

### 2026-11-28 Relay 原图分段读取验收

本机索引服务通过原有媒体网关向来源设备申请只读租约，GUI 在同一条加密连接上分段读取。每次读取核对媒体权限、会话、资源类型、资源 ID、版本及字节范围。关闭连接会释放尚未归还的租约。

隔离测试启动账号服务、来源索引服务及测试代理。代理通过实际 WebSocket 协议和 MediaFileSession 读取授权目录中的 JPEG。两段响应拼接后与源文件逐字节相同。相同设备另开连接复用租约及归还后再读取均被拒绝。媒体流、Relay 及账号路由共 51 项 Release 测试通过。Debug 全解决方案按警告即错误构建通过，零警告、零错误。正式桌面的双击操作和生产部署待验收。

D 盘仍离线。本次验证构建根目录为 C:\Users\a\AppData\Local\Picer\engineering-backups\relay-photo-20261128\verification。其下 src/Picer.Core/bin/Release/net8.0/Picer.Core.dll 的 SHA-256 为 4DAE56978FA71123E352E20CDB383B1C426C07E854829EFB7F3544D412776DBA，server/Picer.IndexService/bin/Release/net8.0/Picer.IndexService.dll 的 SHA-256 为 B6FAFF8794570E1AE3E27AEBBA2883812E153A32FE63731BF7E3D0A96D738740。本阶段未替换正式 GUI。

### 2026-11-28 驻留端缩略图跨重启读取与视频并行分段

当索引服务不能在本机打开照片时，HTTPS 与 Relay 均通过同一个来源预览回调获得 256 像素缩略图。以前回调只写进程内存缓存。本次让回调在未命中内存时读取索引服务已有的加密缩略图目录，并将首次成功获取的 JPEG 原样原子写入。缓存文件按索引记录、来源和内容版本区分，并随对应记录的缩略图清理。

验收使用隔离目录，第一次 HTTPS 请求由测试驻留端返回图片。随后停止服务，使用相同数据根重新启动 IndexService，未启动驻留端，第二次 HTTPS 请求获得与第一次逐字节相同的 JPEG。另验证磁盘内容加密、版本变化时重新获取和按记录清理。

Relay 集成验收使用 2 MiB 加 257 字节的二进制样例，经视频资源路径取得租约。GUI 使用的 ParallelMediaReader 并发读取三段，完整内容及跨块跳读的 128 KiB 均与源文件一致，关闭后原租约失效。此测试覆盖流传输，视频解码与桌面播放另行验收。

Release 验证构建位于 C:\Users\a\AppData\Local\Picer\engineering-backups\relay-photo-20261128\verification\server\Picer.IndexService\bin\Release\net8.0\Picer.IndexService.dll，SHA-256 为 23257A3B44C5DBBE94EE508EA6DDD4543F0739FA2035B18575F41A4023DAD3C3。正式工程所在 D 盘仍离线。

### 2026-11-28 Relay 单条索引读取、来源分页和断线关闭

GUI 的分类建议会按 ID 回读索引，目录浏览也会按来源分页。Relay 现可按 ID 读取照片和视频记录。不存在的记录按普通 HTTP 网关约定返回未找到，连接检查使用的 ID 0 同样适用。来源列表通过已有加密查询传递来源 ID、缺失标志、页大小与游标，沿用本机索引服务的排序与过滤。

隔离账号服务、本机索引和测试驻留端的集成用例验证照片和视频记录、无效 ID、空来源、两页照片的合并顺序及视频来源过滤。断线回归中，在驻留端处理预览期间停止连接器，GUI 失败回调随即关闭网关。原实现会从接收任务的取消回调同步等待同一个接收任务，现场托管栈确认了死锁。现在关闭时立即拒绝新请求并中止套接字，所有读取和在途请求退出后释放会话密钥。集成用例要求关闭回调在 10 秒内结束。

验证构建根目录为 C:\Users\a\AppData\Local\Picer\engineering-backups\relay-photo-20261128\verification。其下 src/Picer.Core/bin/Release/net8.0/Picer.Core.dll 的 SHA-256 为 4462A22E03907AC344ADA5EF18509F9A14F2F81B225DB470D26F1FF638BB4A76，server/Picer.IndexService/bin/Release/net8.0/Picer.IndexService.dll 为 D3C5CB17D14641F6E783D5916B731D23B33E6E447C54AC29ECA7E25ED4C89535。
