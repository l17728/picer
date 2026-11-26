# Relay protocol v1 cryptographic frame primitives (2026-11-26)

This implementation slice defines cryptographic primitives only. The cloud Relay still accepts heartbeat frames only; these frames are not yet routed over a production socket and no GUI query path uses them.

## Key agreement

Each peer creates an ephemeral P-256 ECDH key share bound to the same `spaceId`, `sessionId`, initiator device ID, responder device ID, sender device ID, nonce, creation time, and expiry. The complete share is signed with the sender's existing P-256 device identity key. The receiver must supply the expected peer signing public key from an authenticated account/device registry; a key carried only inside the relay frame is never trusted.

Both peers derive the same 64 bytes using ECDH followed by HKDF-SHA256. The transcript salt includes both ordered public key shares, their nonces and signatures, and all session/tenant/device bindings. HKDF output is split into independent initiator-to-responder and responder-to-initiator AES-256-GCM keys. The Relay can forward the signed public shares but cannot derive these keys.

## Data frames

The AES-GCM associated data binds version, session, space, sender, recipient, sequence, request ID, idempotency key, kind, and expiry. The operation name, API path, query, metadata, and payload belong inside the encrypted payload. Frames are limited to 2 MiB plaintext; sequence numbers are per session and direction. The receiver checks the peer/session/tenant binding and an expiry no more than five minutes ahead before decrypting, then updates a bounded 4096-sequence sliding replay window only after successful authentication. The window allows concurrent streams to arrive out of order while rejecting duplicates and sufficiently old frames.

## Current code and tests

- Primitive implementation: `src/Picer.Core/Identity/RelaySessionCrypto.cs`.
- Tests cover signed two-party agreement, bidirectional encryption, frame binding, tamper detection, invalid peer identity, expiry, replay, and out-of-order sequence acceptance.
- Debug full solution build: 0 warnings, 0 errors. Relay credential/connector and crypto protocol tests: 7/7 passed.
- Release artifact path: `server/Picer.IndexService/bin/Release/net8.0/Picer.IndexService.dll`; SHA-256 `9B6F6495740D546196615A30619C0644302B0A9A0D15DD8FCBD63E740EA0FFE2`.
- Idempotency key is cryptographically bound to a request frame, but no durable request ledger or exactly-once write execution exists yet. This must be implemented at the local IndexService transaction boundary before write operations are routed.
- Peer key lookup, account-service routing, GUI integration, response/chunk multiplexing, cancellation propagation, durable replay state, throughput/backpressure enforcement, and complete tenant revocation behavior remain unimplemented.

Do not enable data forwarding until those pieces are integrated and the E2E cases for RELAY-03, 09, 14, 17, 18, 20, 21, 33, and 35 pass. The protocol layer alone is not full end-to-end Relay acceptance.
