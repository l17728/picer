using System.IO;
using System.Text.Json;

namespace Picer.App;

public static class AppPaths
{
    private static string BootstrapDirectory => Environment.GetEnvironmentVariable("PICER_BOOTSTRAP_ROOT") is { Length: > 0 } bootstrapRoot
        ? Path.GetFullPath(bootstrapRoot)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Picer", "bootstrap");
    private static string DataLocationPath => Path.Combine(BootstrapDirectory, "data-location.json");
    public static string BackendConfigPath => Path.Combine(BootstrapDirectory, "backend.json");
    public static string ServiceEndpointPath => Path.Combine(BootstrapDirectory, "service-endpoint.json");
    public static string AccountCredentialPath => Path.Combine(BootstrapDirectory, "account-device.bin");
    public static string AgentCredentialPath => Path.Combine(BootstrapDirectory, "agent-device.bin");
    private static string RemoteMirrorCleanupPath => Path.Combine(BootstrapDirectory, "pending-remote-mirror-cleanup.json");
    public static bool RequiresAccountSignIn()
    {
        var backend = Picer.Core.Gateway.IndexBackendConfig.Load(BackendConfigPath, ServiceEndpointPath,
            static cipher => System.Security.Cryptography.ProtectedData.Unprotect(cipher, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        if (!backend.DeviceAuthentication) return false;
        var store = new Picer.Core.Identity.WindowsDeviceCredentialStore(AccountCredentialPath);
        if (!store.Exists) return true;
        try { return store.Load().Session is null; }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or InvalidDataException or JsonException or FileNotFoundException)
        { return true; }
    }

    public static Picer.Core.Gateway.IndexBackendOptions LoadIndexBackend()
    {
        var backend = Picer.Core.Gateway.IndexBackendConfig.Load(BackendConfigPath, ServiceEndpointPath,
            static cipher => System.Security.Cryptography.ProtectedData.Unprotect(cipher, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        if (!backend.DeviceAuthentication) return backend;
        var connection = new Picer.Core.Identity.WindowsDeviceCredentialStore(AccountCredentialPath).Load();
        var session = connection.Session ?? throw new InvalidOperationException("请先登录照片空间。");
        return backend with
        {
            BaseUrl = Picer.Core.Identity.DeviceAuthorization.SpaceAddress(connection.Origin, session.SpaceId),
            SpkiPin = Convert.FromBase64String(connection.SpkiPin),
            CachePartition = Picer.Core.Identity.DeviceAuthorization.Digest(connection.ServiceId + ":" + session.AccountId + ":" + session.SpaceId)
        };
    }

    public static void SaveIndexBackendAccount()
    {
        Directory.CreateDirectory(BootstrapDirectory);
        File.WriteAllText(BackendConfigPath, "{\"mode\":\"service\",\"authentication\":\"device\"}");
    }

    public static void SaveIndexBackendLocal()
    {
        Directory.CreateDirectory(BootstrapDirectory);
        File.WriteAllText(BackendConfigPath, "{\"mode\":\"local\",\"syncPolicy\":\"switch-only\"}");
    }

    public static void ScheduleRemoteMirrorCleanup(string root)
    {
        var resolved = Path.GetFullPath(root);
        var dataRoot = Path.GetFullPath(DataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(resolved, dataRoot, StringComparison.OrdinalIgnoreCase) &&
            !resolved.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("remote_mirror_cleanup_outside_data_root");
        Directory.CreateDirectory(BootstrapDirectory);
        File.WriteAllText(RemoteMirrorCleanupPath, JsonSerializer.Serialize(new { root = resolved }));
    }

    public static void CleanupScheduledRemoteMirror()
    {
        if (!File.Exists(RemoteMirrorCleanupPath)) return;
        try
        {
            var document = JsonSerializer.Deserialize<PendingMirrorCleanup>(File.ReadAllText(RemoteMirrorCleanupPath));
            var root = document?.Root is { Length: > 0 } value ? Path.GetFullPath(value) : null;
            var dataRoot = Path.GetFullPath(DataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (root is null || (!string.Equals(root, dataRoot, StringComparison.OrdinalIgnoreCase) &&
                !root.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return;
            foreach (var file in new[] { "remote-mirror.db", "remote-mirror.db-wal", "remote-mirror.db-shm", "remote-geocoding-cache.json" })
                DeleteFile(Path.Combine(root, file));
            foreach (var directory in new[] { "remote-cache", "remote-thumbs", "remote-face-thumbs", "remote-video-summary-cache" })
                DeleteDirectory(Path.Combine(root, directory));
            File.Delete(RemoteMirrorCleanupPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Leave the marker for the next startup; a failed cleanup must not
            // prevent the local index from opening.
        }
    }

    private static void DeleteFile(string path) { if (File.Exists(path)) File.Delete(path); }
    private static void DeleteDirectory(string path) { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    private sealed record PendingMirrorCleanup(string Root);

    public static void SaveIndexBackendService(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip.Trim(), out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("请输入有效的 IPv4 地址。", nameof(ip));
        Directory.CreateDirectory(BootstrapDirectory);
        File.WriteAllText(BackendConfigPath, JsonSerializer.Serialize(new { mode = "service", indexServiceIp = ip.Trim() }));
    }
    public static string RuntimeRoot => Path.Combine(AppContext.BaseDirectory, "runtime");

    public static string DataRoot
    {
        get
        {
            if (Environment.GetEnvironmentVariable("PICER_DATA_ROOT") is { Length: > 0 } isolatedRoot) return ResolveConfiguredPath(isolatedRoot);
            try { if (File.Exists(DataLocationPath) && JsonSerializer.Deserialize<DataLocation>(File.ReadAllText(DataLocationPath)) is { Path.Length: > 0 } configured) return ResolveConfiguredPath(configured.Path); } catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "picer");
        }
    }
    public static void SetDataRoot(string path) { Directory.CreateDirectory(BootstrapDirectory); File.WriteAllText(DataLocationPath, JsonSerializer.Serialize(new DataLocation(path.Trim()))); }
    private static string ResolveConfiguredPath(string path) => Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

    public static string DbPath => Path.Combine(DataRoot, "picer.db");

    public static string ThumbnailDir => Path.Combine(DataRoot, "thumbs");

    public static string ModelRoot => Environment.GetEnvironmentVariable("PICER_MODEL_ROOT") is { Length: > 0 } modelRoot
        ? Path.GetFullPath(modelRoot)
        : Path.Combine(RuntimeRoot, "models", "face");

    public static string YuNetModelPath => Path.Combine(ModelRoot, "face_detection_yunet_2023mar.onnx");

    public static string SFaceModelPath => Path.Combine(ModelRoot, "face_recognition_sface_2021dec.onnx");

    public static string FaceThumbnailDir => Path.Combine(DataRoot, "face-thumbs");

    public static string BundledFfmpegPath => Path.Combine(RuntimeRoot, "ffmpeg", "ffmpeg.exe");
    public static string BundledWhisperPath => Path.Combine(RuntimeRoot, "whisper", "whisper-cli.exe");
    public static string BundledWhisperModelPath => Path.Combine(RuntimeRoot, "models", "whisper", "ggml-base-q5_1.bin");
    public static string ChineseClipModelPath => Path.Combine(RuntimeRoot, "models", "chinese-clip", "model_quantized.onnx");
    public static string ChineseClipVocabularyPath => Path.Combine(RuntimeRoot, "models", "chinese-clip", "vocab.txt");
    public static string FlorenceModelRoot => Path.Combine(RuntimeRoot, "models", "florence-2-base-ft");
    public static string FlorenceRuntimeSettingsPath => Path.Combine(DataRoot, "florence-runtime.json");

    public static string CloudVisionSettingsPath => Path.Combine(DataRoot, "cloud-vision.json");
    public static string DirectoryClassificationSettingsPath => Path.Combine(DataRoot, "directory-classification.json");
    private sealed record DataLocation(string Path);
}
