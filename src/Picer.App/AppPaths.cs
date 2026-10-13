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
        File.WriteAllText(BackendConfigPath, "{\"mode\":\"local\"}");
    }

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
