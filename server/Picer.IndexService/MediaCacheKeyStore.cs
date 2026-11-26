using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Picer.IndexService;

/// <summary>Maintains one protected, stable cache key inside each isolated IndexService data root.</summary>
internal static class MediaCacheKeyStore
{
    public static byte[] LoadOrCreate(string dataRoot, string tenantScope)
    {
        if (string.IsNullOrWhiteSpace(tenantScope) || tenantScope.Any(char.IsControl))
            throw new ArgumentException("invalid_media_cache_scope", nameof(tenantScope));
        var root = Path.GetFullPath(dataRoot);
        var keyRing = Path.Combine(root, "media-keyring");
        Directory.CreateDirectory(keyRing);
        RestrictDirectory(keyRing);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keyRing), options =>
        {
            options.SetApplicationName("Picer.MediaCache." + tenantScope);
            if (OperatingSystem.IsWindows()) options.ProtectKeysWithDpapi();
        });
        var protector = provider.CreateProtector("media-cache-key-v1", tenantScope);
        var path = Path.Combine(root, "media-cache-key.protected");
        if (File.Exists(path)) return Validate(protector.Unprotect(File.ReadAllBytes(path)));

        var generated = RandomNumberGenerator.GetBytes(32);
        var protectedKey = protector.Protect(generated);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                output.Write(protectedKey);
                output.Flush(flushToDisk: true);
            }
            RestrictFile(temporary);
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
            return Validate(protector.Unprotect(File.ReadAllBytes(path)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(generated);
            CryptographicOperations.ZeroMemory(protectedKey);
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    private static byte[] Validate(byte[] key)
    {
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new InvalidDataException("media_cache_key_invalid");
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
