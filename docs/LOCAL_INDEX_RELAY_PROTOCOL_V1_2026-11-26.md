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
