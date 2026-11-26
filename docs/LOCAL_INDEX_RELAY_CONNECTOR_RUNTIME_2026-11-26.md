# Local IndexService Relay Connector Runtime (2026-11-26)

## Implemented

- Added a Linux-capable credential store for the local IndexService. It atomically persists the relay device grant and exportable P-256 private key, verifies the key pair on load, and rejects relay grants broader than the exact `relay.connect` scope.
- On Unix, the credential directory is set to mode `0700` and the credential file to `0600`; a credential file with group/world permissions is rejected. The file is not encrypted by this layer, so deployment must keep it on a service-owned local filesystem with protected backups.
- Added `BeginEnrollmentAsync` and `ImportAuthorizationAsync` operations around the existing device approval flow. The cloud account owner still explicitly approves the `index-service` device.
- The standalone local IndexService starts one background connector when `relay-connector.json` exists under its data root (override with `PICER_INDEX_RELAY_CREDENTIAL_PATH`). It opens the pinned HTTPS/WebSocket connection, sends a heartbeat every 15 seconds, validates acknowledgements, and retries with capped exponential backoff and jitter. Shutdown cancels the socket and heartbeat loop.
- Added the token-protected `GET /api/index-relay/v1/status` endpoint. Isolated tenant IndexService runtimes do not start this connector.

## Verification

- `dotnet build picer.sln -c Debug --no-restore --warnaserror`: 0 warnings, 0 errors.
- Focused credential-store and relay-connector tests: 4/4 passed. They cover owner-only file modes, private/public key binding rejection, the approved relay-only enrollment flow, a live persistent WebSocket heartbeat, status reporting, and clean shutdown.
- `dotnet build server/Picer.IndexService/Picer.IndexService.csproj -c Release --no-restore --warnaserror`: 0 warnings, 0 errors.
- Release DLL: `server/Picer.IndexService/bin/Release/net8.0/Picer.IndexService.dll`; SHA-256 `F4F73443837A1F33AA223D02CBE76662A56574E7BBF3C731A4F4A7B08E2150DD`.

## Still required for the Relay feature

This closes the local connector identity and heartbeat runtime only. It does not yet forward index requests, multiplex GUI requests and responses, define the encrypted payload envelope, provide retry/idempotency for data requests, or prove full tenant/device/session isolation end-to-end. No production service was deployed because the connector currently carries only heartbeats. RELAY-01 through RELAY-40 remain subject to their individual E2E acceptance cases.
