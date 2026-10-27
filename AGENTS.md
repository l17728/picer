# AGENTS.md

Picer's GUI and MediaAgent are Windows x64 .NET 8 applications; IndexService and ModelServer also ship Linux x64 binaries. Product UI and docs under `docs/` (including `docs/SDD.md`, the architecture spec) are written in Chinese; E2E tests assert on Chinese UI strings. Current capabilities and paused acceptance work are recorded in `docs/CURRENT_CAPABILITIES_2026-10-09.md` and `docs/DEVELOPMENT_PLAN.md`; dated reports preserve historical evidence, not current task assignments.

## Projects

- `src/Picer.Core` — net8.0 library: indexing, SQLite data layer (`Data/`), search/FTS5, vision/ONNX (`Vision/`), people, thumbnails, storage (local/WebDAV), file operations.
- `src/Picer.App` — WPF UI (net8.0-windows), CommunityToolkit.Mvvm.
- `src/Picer.MediaAgent` — Windows resident source agent: authorized media reads, scanning, video tools and model endpoint forwarding; bundled with the GUI or installed separately.
- `server/Picer.IndexService` — HTTPS index gateway with authorized index sync, analysis, repair and index-purge endpoints; original media stays read-only. See `docs/INDEX_SERVICE_CS_DESIGN_2026-09-08.md`; gateway contracts live in `src/Picer.Core/Gateway/`.
- `server/Picer.ModelServer` — separate ASP.NET Core API (deployed independently, has its own `Dockerfile` + `server/README.md`). Run: `dotnet run --project server/Picer.ModelServer`.
- `tests/Picer.Tests` (xUnit) and `tests/Picer.E2E` (drives the real WPF app via UI Automation and the real Windows recycle bin; WPF tests use STA threads).
- `tools/` — benchmarks, ONNX probes, `verify-release.ps1`, `publish-self-contained.ps1`.

## Build and test (match CI exactly)

```powershell
dotnet restore picer.sln
dotnet build picer.sln -c Debug --no-restore --warnaserror
dotnet test tests/Picer.Tests/Picer.Tests.csproj -c Debug --no-build --no-restore --filter "Category!=RuntimeComponent"
dotnet test tests/Picer.E2E/Picer.E2E.csproj -c Debug --no-build --no-restore --filter "Category!=RuntimeComponent"
```

- CI builds with `--warnaserror`: fix warnings, never suppress new ones.
- All projects target x64 (`PlatformTarget`); native ONNX DirectML/OpenCV requires it — do not change.
- `Directory.Build.props/targets` remove OpenCvSharp's analyzer (built for newer Roslyn than the pinned SDK 8) and suppress CS9057. Leave as is.

## RuntimeComponent tests and offline runtime

Tests tagged `[Trait("Category", "RuntimeComponent")]` need pinned offline assets in `third_party/runtime` (YuNet/SFace ONNX, FFmpeg, whisper.cpp, Chinese-CLIP, Florence-2) verified by exact SHA-256 (`RuntimeComponentService.RequiredComponents`, `tools/publish-self-contained.ps1`). CI and the ordinary release workflow exclude this category. The separate `offline-release.yml` workflow downloads `Picer-runtime-win-x64.zip` from `runtime-v1`, applies the verified overlay, publishes through the pinned asset/license gates, and runs `VideoStreamDecodeTests`. These assets are git-ignored — a fresh clone won't have them. Passing that one offline test does not mean every RuntimeComponent test ran.

For face-model tests alone, `tools/Picer.VisionProbe/download-models.ps1` fetches YuNet/SFace into `PICER_MODEL_ROOT`.

## Environment variables

- `PICER_DATA_ROOT` — redirects all app data (SQLite db, thumbs) to an isolated directory; E2E tests set this per-test.
- `PICER_SCAN_CACHE_ROOT` — overrides persistent directory manifests and NTFS USN checkpoints; defaults to `scan-manifests` under the app data root. Treat as disposable cache and invalidate it on explicit index purge.
- `PICER_TEST_TEMP` — overrides TEMP/TMP for tests (set via `[ModuleInitializer]` in each test project). Default resolves from the repo-root `picer.paths.json` (`testTemp` = `artifacts/test-temp`, repo-relative; per-project `tests`/`e2e` subdirectory).
- `PICER_MODEL_ROOT` — face model directory; vision tests default to `third_party/model-probe` via `picer.paths.json` (`modelProbeRoot`).
- `PICER_FFMPEG_PATH` — FFmpeg override used by `Picer.Core.Video.VideoIndexService`.
- `PICER_MODEL_SERVER_API_KEY` — bearer token required by ModelServer `/api/*` routes.
- `PICER_INDEX_DATA_ROOT` / `PICER_INDEX_SERVICE_PORT` / `PICER_INDEX_SERVICE_TOKEN` — data root, port (default 8445) and token for `Picer.IndexService`.
- `PICER_INDEX_SERVICE_RENEW_CERT=1` — rotates the service HTTPS certificate on next start (clients must re-confirm the SPKI pin).

## Architecture invariants (from docs/SDD.md — enforced by tests)

- Indexing, search, and analysis must never modify, move, or delete original files. All destructive file operations go through `IUserAuthorizedFileOperation` (recycle bin), re-verifying path/size/mtime/hash at execution time; that flow removes the index record only after verified recycle success. Separately authorized index-only purge removes the selected reference and its owned data without deleting originals; shared content survives until its last reference is removed. Missing files never trigger automatic index purge.
- Destructive flows follow the state machine `Detected -> Reviewed -> Confirmed -> Executing -> Verified -> Completed/PartiallyFailed`. Candidates/suggestions never auto-confirm.
- SQLite schema changes are chained `PRAGMA user_version` migrations in `src/Picer.Core/Data/PicerDatabase.cs` (currently up to v91; deployed databases may be older). Migrations require a whole-file backup of `picer.db` + WAL + SHM with all writers closed; `DatabaseMigrationSafety` enforces staged migration and recoverable publication.
- Privacy: SQL must be parameterized; never log credentials, face embeddings, person names/aliases, or AI question text. Remote/cloud features are opt-in and independently authorized; the full flow must work offline.
- Vision embeddings/thresholds are per model version and must never be compared or mixed across versions.

## Conventions

- Commits follow Conventional Commits (`feat:`, `fix:`, `docs:`, `refactor:`, `chore:`), lowercase, no scope. CI runs on `main`; `v*` tags trigger a draft release. Offline assets, installer/deployment evidence and the unified checksum manifest are verified before publishing the draft. Do not move published tags or claim a private repository release is anonymously downloadable.
- Feature work is documented with a dated implementation report in `docs/` (e.g. `FACE_EVAL_005_IMPLEMENTATION_2026-09-07.md`).
- `third_party/runtime/` is content-linked into the WPF app; `tools/publish-self-contained.ps1` fails if any pinned asset or license file is missing or hash-mismatched.
