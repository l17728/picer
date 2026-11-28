# Relay protocol v1 cryptographic frame primitives (2026-11-26)

This implementation slice defines cryptographic primitives only. The cloud Relay still accepts heartbeat frames only; these frames are not yet routed over a production socket and no GUI query path uses them.

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
- The multiplexer accepts single-frame and bounded multi-frame responses. Peer key lookup, account-service routing, GUI integration, cancellation propagation at the local IndexService, durable replay state, throughput/backpressure enforcement across tenants, and complete tenant revocation behavior remain unimplemented.

Do not enable data forwarding until those pieces are integrated and the E2E cases for RELAY-03, 09, 14, 17, 18, 20, 21, 33, and 35 pass. The protocol layer alone is not full end-to-end Relay acceptance.

2026-11-28 验证了请求排队超时。并发槽位用尽时，等待时间计入同一个请求期限。排队超时不会发送请求帧或取消帧，已占用槽位的请求可以继续执行。相关协议测试 5 项通过。

### 2026-11-28 加密多帧响应

协议层支持在同一请求内返回最多 8 MiB。正文不超过 2 MiB 时沿用单个 response 帧；更大的正文全部使用 chunk 帧。每块的加密正文以两个大端序 32 位整数起始，依次表示总长度和字节偏移。除末块外，每块数据长度均为 2 MiB 减 8 字节。发送端逐帧等待传输，接收端核对请求、幂等键、总长度和连续偏移，收齐才交付。单个复用器为未收齐响应预留的空间合计上限为 32 MiB。该协议适用于有界响应，任意长度的媒体流仍需独立验收。

Release 定向测试涵盖空响应、单帧临界值、超过单帧一个字节、8 MiB 完整传输、超过上限及缺首块。帧经过加密、线上格式编解码、解密与组装。正式构建路径为 `D:\picer\src\Picer.Core\bin\Release\net8.0\Picer.Core.dll`，本阶段 SHA-256 为 `003D5D6057C13C6044746E3C12BA0DD5C3C66A19B61A508999F24C9B36ABC4E3`。
