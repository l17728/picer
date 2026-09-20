using System.Text;
using System.Text.Json;

namespace Picer.Core.Gateway;

public sealed record IndexBackendOptions(string Mode, string? BaseUrl, byte[]? SpkiPin, string? Token, string? PipeName = null);

public static class IndexBackendConfig
{
    public const string LocalMode = "local";
    public const string ServiceMode = "service";

    private sealed record BackendFile(string? Mode, string? BaseUrl, string? IndexServiceIp);
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
                baseUrlOverride = string.IsNullOrWhiteSpace(backend?.IndexServiceIp)
                    ? backend?.BaseUrl
                    : BuildIndexServiceUrl(backend.IndexServiceIp);
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
        else if (endpoint.TokenCipher.StartsWith("plain:", StringComparison.OrdinalIgnoreCase))
        {
            token = Encoding.UTF8.GetString(Convert.FromBase64String(endpoint.TokenCipher["plain:".Length..]));
        }
        else
        {
            if (unprotect is null) throw new InvalidOperationException("service 模式缺少 token 解密委托。");
            var base64 = endpoint.TokenCipher.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase)
                ? endpoint.TokenCipher["dpapi:".Length..]
                : endpoint.TokenCipher;
            token = Encoding.UTF8.GetString(unprotect(Convert.FromBase64String(base64)));
        }

        var baseUrl = string.IsNullOrWhiteSpace(baseUrlOverride) ? endpoint.Url : baseUrlOverride;
        return new IndexBackendOptions(ServiceMode, baseUrl, Convert.FromBase64String(endpoint.SpkiPin), token, endpoint.PipeName);
    }

    private static string BuildIndexServiceUrl(string ip)
    {
        var host = ip.Trim();
        if (host.Length == 0 || host.Contains('/', StringComparison.Ordinal) || host.Contains(':', StringComparison.Ordinal))
            throw new InvalidOperationException("indexServiceIp 必须是 IPv4 地址；端口和 API 路径由程序固定组装。");
        if (!System.Net.IPAddress.TryParse(host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("indexServiceIp 不是有效的 IPv4 地址。");
        return $"https://{host}:8445";
    }
}
