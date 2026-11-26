using System.Security.Cryptography;

namespace Picer.Core.Security;

/// <summary>Stores a stable per-Windows-user key for encrypted client-side media caches.</summary>
public static class ClientMediaCacheKeyStore
{
    private const string KeyFileName = "media-cache-key.dpapi";

    public static byte[] LoadOrCreate(string dataRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("client_media_cache_requires_windows_dpapi");
        var root = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, KeyFileName);
        if (File.Exists(path)) return Unprotect(File.ReadAllBytes(path));

        var key = RandomNumberGenerator.GetBytes(32);
        var protectedKey = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                output.Write(protectedKey);
                output.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
            return Unprotect(File.ReadAllBytes(path));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(protectedKey);
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    private static byte[] Unprotect(byte[] protectedKey)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("client_media_cache_requires_windows_dpapi");
        try
        {
            var key = ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
            if (key.Length == 32) return key;
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidDataException("client_media_cache_key_invalid");
        }
        finally { CryptographicOperations.ZeroMemory(protectedKey); }
    }
}
