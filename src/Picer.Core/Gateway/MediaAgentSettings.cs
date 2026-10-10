using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Formats.Asn1;

namespace Picer.Core.Gateway;

public sealed record MediaAgentSettings(string ServiceUrl, string Token, string SpkiPin, string[] Roots)
{
    private sealed record Stored(string ServiceUrl, string TokenCipher, string SpkiPin, string[] Roots);

    public static string NormalizeServiceUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("请输入 HTTPS 服务根地址，例如 https://服务器:8445。");
        value = value.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = value.TrimEnd('/');
            if (System.Net.IPAddress.TryParse(value, out var ip)) value = new UriBuilder("https", ip.ToString(), 8445).Uri.AbsoluteUri;
            else value = "https://" + value + (value.Contains(':') ? "" : ":8445");
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("请输入 HTTPS 服务根地址，例如 https://服务器:8445。");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public MediaAgentSettings Validate()
    {
        var url = NormalizeServiceUrl(ServiceUrl);
        if (string.IsNullOrWhiteSpace(Token) || Token.Any(char.IsControl)) throw new ArgumentException("请填写有效的服务令牌。");
        _ = ValidateSpkiPin(SpkiPin);
        if (Roots is null) throw new ArgumentException("请至少选择一个允许读取的媒体目录。");
        var roots = Roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0) throw new ArgumentException("请至少选择一个允许读取的媒体目录。");
        return this with { ServiceUrl = url, Roots = roots };
    }

    public static byte[] ValidateSpkiPin(string encoded)
    {
        var pin = Convert.FromBase64String(encoded);
        try
        {
            var reader = new AsnReader(pin, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            var algorithm = sequence.ReadSequence();
            _ = algorithm.ReadObjectIdentifier();
            if (algorithm.HasData) _ = algorithm.ReadEncodedValue();
            algorithm.ThrowIfNotEmpty();
            var key = sequence.ReadBitString(out var unusedBits);
            if (key.Length == 0 || unusedBits != 0) throw new AsnContentException();
            sequence.ThrowIfNotEmpty(); reader.ThrowIfNotEmpty();
            return pin;
        }
        catch (AsnContentException exception) { throw new ArgumentException("请先配对服务证书；保存的公钥格式无效。", exception); }
    }

    public void Save(string path, Func<byte[], byte[]> protect)
    {
        var settings = Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stored = new Stored(settings.ServiceUrl, Convert.ToBase64String(protect(Encoding.UTF8.GetBytes(settings.Token))), settings.SpkiPin, settings.Roots);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(stored)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static MediaAgentSettings? Load(string path, Func<byte[], byte[]> unprotect)
    {
        if (!File.Exists(path)) return null;
        var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) ?? throw new InvalidDataException("代理设置无效。");
        return new MediaAgentSettings(stored.ServiceUrl, Encoding.UTF8.GetString(unprotect(Convert.FromBase64String(stored.TokenCipher))), stored.SpkiPin, stored.Roots).Validate();
    }

    public static MediaAgentSettings? FromEnvironment(Func<string, string?> get)
    {
        var address = get("PICER_INDEX_SERVICE_IP");
        if (string.IsNullOrWhiteSpace(address)) return null;
        return new MediaAgentSettings(address, get("PICER_INDEX_SERVICE_TOKEN") ?? "", get("PICER_INDEX_SERVICE_SPKI_PIN") ?? "",
            (get("PICER_MEDIA_AGENT_ROOTS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Validate();
    }

    public static string ResolveFfmpeg(string? configured, string baseDirectory)
    {
        var candidates = new[] { configured, Path.Combine(baseDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
            Path.Combine(baseDirectory, "..", "tools", "ffmpeg", "ffmpeg.exe"), Path.Combine(baseDirectory, "..", "runtime", "ffmpeg", "ffmpeg.exe") };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
            File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "ffprobe.exe"))) ?? "";
    }

    // Discovery performs only a TLS handshake: credentials cannot be sent before confirmation.
    public static async Task<byte[]> DiscoverSpkiAsync(string address, CancellationToken cancellationToken)
    {
        var uri = new Uri(NormalizeServiceUrl(address));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port, cancellationToken).ConfigureAwait(false);
        byte[]? discovered = null;
        using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            using var cert = new X509Certificate2(certificate);
            discovered = HttpIndexGateway.ComputeSpkiPin(cert);
            return true;
        });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.Host }, cancellationToken).ConfigureAwait(false);
        return discovered ?? throw new IOException("服务未提供证书。");
    }

    public static bool MatchesCertificate(X509Certificate? certificate, byte[]? pin)
    {
        if (certificate is null || pin is null || pin.Length == 0) return false;
        using var cert = new X509Certificate2(certificate);
        return CryptographicOperations.FixedTimeEquals(HttpIndexGateway.ComputeSpkiPin(cert), pin);
    }
}
