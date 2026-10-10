using System.Security.Cryptography;
using System.Text;

namespace Picer.Core.Gateway;

public static class MediaSourceIdentity
{
    // Same open handle as the reader; Windows share mode excludes writers and replacement.
    // When strong identity is unavailable a new session identity prevents unsafe cross-request reuse.
    public static string GetETag(FileStream stream)
    {
        var signature = Picer.Core.Video.LocalVideoContentHashCache.ReadSignature(stream.Name, stream);
        if (signature is null) return "W/\"" + Guid.NewGuid().ToString("N") + "\"";
        var identity = signature.Value.ToString();
        return "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant() + "\"";
    }
    public static string ResolveAllowed(string path, IReadOnlyList<string> roots)
    {
        var full = Path.GetFullPath(path);
        if (!roots.Any(root => full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("media_path_not_authorized");
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("media_link_not_authorized");
        return full;
    }
}
