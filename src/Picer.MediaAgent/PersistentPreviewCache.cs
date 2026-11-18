using System.Security.Cryptography;
using System.Text;

namespace Picer.MediaAgent;

/// <summary>Source-side disk cache for generated previews.</summary>
internal sealed class PersistentPreviewCache
{
    private const long MaxBytes = 512L * 1024 * 1024;
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Picer", "media-agent", "previews");

    public PersistentPreviewCache() => Directory.CreateDirectory(_root);

    public byte[]? TryGet(string path, int size)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var file = Path.Combine(_root, Key(path, size, info.Length, info.LastWriteTimeUtc) + ".jpg");
            if (!File.Exists(file)) return null;
            File.SetLastAccessTimeUtc(file, DateTime.UtcNow);
            return File.ReadAllBytes(file);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Put(string path, int size, ReadOnlySpan<byte> bytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || bytes.Length == 0) return;
            var destination = Path.Combine(_root, Key(path, size, info.Length, info.LastWriteTimeUtc) + ".jpg");
            if (File.Exists(destination)) return;
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            File.WriteAllBytes(temporary, bytes.ToArray());
            File.Move(temporary, destination, overwrite: false);
            Trim();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Trim()
    {
        var files = Directory.EnumerateFiles(_root, "*.jpg").Select(path => new FileInfo(path))
            .OrderBy(file => file.LastAccessTimeUtc).ToList();
        long total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total <= MaxBytes) break;
            try { total -= file.Length; file.Delete(); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Key(string path, int size, long length, DateTime lastWriteUtc)
    {
        var value = $"v1|{size}|{length}|{lastWriteUtc.Ticks}|{Path.GetFullPath(path).ToUpperInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
