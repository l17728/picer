using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace Picer.IndexService.Identity;

public static class IdentitySigningKey
{
    public static ECDsa LoadOrCreate(string root, string serviceId)
    {
        var secrets = Directory.CreateDirectory(Path.Combine(root, "secrets"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(secrets.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var keys = Directory.CreateDirectory(Path.Combine(secrets.FullName, "signing-protection"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keys.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var protector = DataProtectionProvider.Create(keys, options =>
        {
            options.SetApplicationName("Picer.Signing." + serviceId);
            if (OperatingSystem.IsWindows()) options.ProtectKeysWithDpapi();
        }).CreateProtector("signing-key-v1");
        var path = Path.Combine(secrets.FullName, "authorization-signing.key");
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            if (File.Exists(path))
            {
                var plain = protector.Unprotect(File.ReadAllBytes(path));
                try
                {
                    key.ImportPkcs8PrivateKey(plain, out var consumed);
                    if (consumed != plain.Length || key.KeySize != 256) throw new InvalidDataException("invalid_signing_key");
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            else
            {
                var plain = key.ExportPkcs8PrivateKey();
                try
                {
                    var protectedKey = protector.Protect(plain);
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                    stream.Write(protectedKey); stream.Flush(true);
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            return key;
        }
        catch { key.Dispose(); throw; }
    }
}
