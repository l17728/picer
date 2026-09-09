using System.Text;
using System.Text.Json;

namespace Picer.Core.Gateway;

public sealed record IndexBackendOptions(string Mode, string? BaseUrl, byte[]? SpkiPin, string? Token, string? PipeName = null);

public static class IndexBackendConfig
{
    public const string LocalMode = "local";
    public const string ServiceMode = "service";

    private sealed record BackendFile(string? Mode, string? BaseUrl);
    private sealed record EndpointFile(string Url, string SpkiPin, string TokenCipher, string? PipeName);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static IndexBackendOptions Load(string backendJsonPath, string? serviceEndpointJsonPath, Func<byte[], byte[]>? unprotect = null)
    {
        string? mode = null;
        string? baseUrlOverride = null;
        try
        {
            if (File.Exists(backendJsonPath))
            {
                var backend = JsonSerializer.Deserialize<BackendFile>(File.ReadAllText(backendJsonPath), Json);
                mode = backend?.Mode;
                baseUrlOverride = backend?.BaseUrl;
            }
        }
        catch
        {
            return new IndexBackendOptions(LocalMode, null, null, null);
        }

        if (!string.Equals(mode, ServiceMode, StringComparison.OrdinalIgnoreCase))
            return new IndexBackendOptions(LocalMode, null, null, null);

        if (string.IsNullOrWhiteSpace(serviceEndpointJsonPath) || !File.Exists(serviceEndpointJsonPath))
            throw new InvalidOperationException("service 模式需要 service-endpoint.json。");
        var endpoint = JsonSerializer.Deserialize<EndpointFile>(File.ReadAllText(serviceEndpointJsonPath), Json)
            ?? throw new InvalidOperationException("service-endpoint.json 无法解析。");

        var environmentToken = Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN");
        string token;
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            token = environmentToken;
        }
        else
        {
            if (unprotect is null) throw new InvalidOperationException("service 模式缺少 token 解密委托。");
            token = Encoding.UTF8.GetString(unprotect(Convert.FromBase64String(endpoint.TokenCipher)));
        }

        var baseUrl = string.IsNullOrWhiteSpace(baseUrlOverride) ? endpoint.Url : baseUrlOverride;
        return new IndexBackendOptions(ServiceMode, baseUrl, Convert.FromBase64String(endpoint.SpkiPin), token, endpoint.PipeName);
    }
}
