namespace Picer.Core.Identity;

/// <summary>A logical UNC namespace keeps equal paths from different publishers distinct in existing catalogs and mirrors.</summary>
public static class PublishedMediaPath
{
    private const string Prefix = @"\\Picer-";

    public static string Qualify(string publisherId, string original)
    {
        if (!Guid.TryParseExact(publisherId, "N", out _) || string.IsNullOrWhiteSpace(original) || original.Any(char.IsControl))
            throw new ArgumentException("invalid_published_path");
        if (original.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("nested_published_path");
        var path = original.Replace('/', '\\');
        string suffix;
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
            suffix = char.ToUpperInvariant(path[0]) + "$" + path[2..];
        else if (path.StartsWith(@"\\", StringComparison.Ordinal)) suffix = "UNC\\" + path[2..];
        else if (original.StartsWith('/')) suffix = "Unix\\" + original.TrimStart('/').Replace('/', '\\');
        else throw new ArgumentException("absolute_source_path_required");
        if (suffix.Split('\\').Any(segment => segment is "." or "..")) throw new ArgumentException("invalid_source_path_segments");
        return Prefix + publisherId + "\\" + suffix;
    }

    public static bool TryUnqualify(string path, out string publisherId, out string original)
    {
        publisherId = ""; original = path;
        var normalized = path.Replace('/', '\\');
        if (!normalized.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var components = normalized[Prefix.Length..].Split('\\', 2);
        if (components.Length != 2 || !Guid.TryParseExact(components[0], "N", out _)) return false;
        var suffix = components[1];
        if (suffix.Split('\\').Any(segment => segment is "." or "..")) return false;
        if (suffix.StartsWith("UNC\\", StringComparison.Ordinal)) original = @"\\" + suffix[4..];
        else if (suffix.StartsWith("Unix\\", StringComparison.Ordinal)) original = "/" + suffix[5..].Replace('\\', '/');
        else if (suffix.Length >= 3 && char.IsAsciiLetter(suffix[0]) && suffix[1] == '$' && suffix[2] == '\\') original = suffix[0] + ":" + suffix[2..];
        else return false;
        publisherId = components[0];
        return true;
    }
}
