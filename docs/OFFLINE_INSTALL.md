# Picer offline installation

1. Download `Picer-v<version>-win-x64-full.zip` and `Picer-runtime-v<version>-win-x64.zip` from the same GitHub release. Keep `SHA256SUMS-OFFLINE.txt` beside them and verify both package hashes before extraction.
2. Extract the full GUI archive to a new folder. It contains the self-contained GUI and its local MediaAgent; no system .NET, Python, CUDA, or FFprobe installation is required.
3. Start `Picer.App.exe`. For a remote IndexService, select the service URL and confirm its certificate fingerprint. The GUI communicates with the IndexService; a local MediaAgent is only needed when this computer provides local media files or analysis.
4. The separate runtime archive contains pinned models and native components for offline component setup. Extract it only into the `runtime` directory when following the component-specific instructions; do not merge it over user data.

The archives contain programs and runtime dependencies only. They do not contain account credentials, service tokens, certificates, indexes, or original media. Keep the files from one release together and retain `OFFLINE-RELEASE-MANIFEST.json` for provenance.
