using System.Text.Json;
using System.Text.Json.Serialization;

namespace Picer.Core.Storage;

public sealed class StorageConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "local";

    [JsonPropertyName("local")]
    public LocalConfig? Local { get; set; }

    [JsonPropertyName("webdav")]
    public WebDAVConfig? WebDAV { get; set; }

    [JsonPropertyName("cache")]
    public CacheConfig? Cache { get; set; }

    public static StorageConfig Load(string path)
    {
        if (!File.Exists(path))
            return new StorageConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<StorageConfig>(json) ?? new StorageConfig();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (WebDAV is not null) WebDAV.Password = "";
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public static string? ReadLegacyWebDavPassword(string path)
    {
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("webdav", out var webdav) &&
               webdav.TryGetProperty("password", out var password)
            ? password.GetString() : null;
    }

    public IStorageProvider CreateProvider(string cacheDir, string? webDavPassword = null)
    {
        IStorageProvider inner = Type.ToLowerInvariant() switch
        {
            "webdav" when WebDAV != null => new WebDAVStorageProvider(
                WebDAV.ServerUrl,
                WebDAV.Username,
                webDavPassword ?? WebDAV.Password,
                WebDAV.BasePath),
            _ => new LocalStorageProvider()
        };

        if (Cache?.Enabled == true)
            return new CachedStorageProvider(inner, cacheDir, Math.Clamp(Cache.MaxSizeGb, 1, 1000) * 1024L * 1024 * 1024);

        return inner;
    }
}

public sealed class LocalConfig
{
    [JsonPropertyName("root")]
    public string Root { get; set; } = "";
}

public sealed class WebDAVConfig
{
    [JsonPropertyName("server_url")]
    public string ServerUrl { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("base_path")]
    public string BasePath { get; set; } = "/";
}

public sealed class CacheConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("directory")]
    public string Directory { get; set; } = "";

    [JsonPropertyName("max_size_gb")]
    public int MaxSizeGb { get; set; } = 10;
}
