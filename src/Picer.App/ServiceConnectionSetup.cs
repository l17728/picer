using System.IO;
using Picer.Core.Gateway;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Picer.App;

public sealed class ServiceConnectionSetup(string baseUrl, string token, byte[] spkiPin)
{
    public string BaseUrl { get; } = baseUrl;
    public string Token { get; } = token;
    public byte[] SpkiPin { get; } = spkiPin;
    public static ServiceConnectionSetup Parse(string address, string token, string fingerprint)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("请输入 HTTPS 服务地址，例如 https://服务器地址:8445，不包含路径、账户或查询参数。");
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new ArgumentException("请输入管理员提供的访问令牌。");
        byte[] pin;
        try { pin = MediaAgentSettings.ValidateSpkiPin(fingerprint.Trim()); }
        catch (FormatException) { throw new ArgumentException("证书公钥格式无效，请重新获取并确认服务证书。"); }
        return new(uri.GetLeftPart(UriPartial.Authority), token.Trim(), pin);
    }

    public void Save(string backendPath, string endpointPath)
    {
        // Write endpoint first: an incomplete save must not enable an unpaired service mode.
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(Token), null, DataProtectionScope.CurrentUser);
        WriteAtomic(endpointPath, JsonSerializer.Serialize(new
        {
            // Preserve the historical endpoint contract for case-sensitive agent readers.
            Url = BaseUrl, SpkiPin = Convert.ToBase64String(SpkiPin),
            TokenCipher = "dpapi:" + Convert.ToBase64String(cipher)
        }));
        WriteAtomic(backendPath, JsonSerializer.Serialize(new { mode = "service", baseUrl = BaseUrl }));
    }

    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
