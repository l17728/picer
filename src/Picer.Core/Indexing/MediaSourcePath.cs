namespace Picer.Core.Indexing;

/// <summary>Logical source paths must not be resolved against another machine's working directory.</summary>
public static class MediaSourcePath
{
    public static string Normalize(string path)
    {
        var value = path.Replace('\\', '/');
        const string prefix = "/opt/picer-index/";
        var start = value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? prefix.Length
            : value.Length > 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' &&
              value.AsSpan(2).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? prefix.Length + 2 : 0;
        if (start > 0 && value.Length > start + 2 && char.IsAsciiLetter(value[start]) &&
            value[start + 1] == ':' && value[start + 2] == '/') value = value[start..];
        return value.TrimEnd('/');
    }

    public static string ForStorage(string path)
    {
        var value = Normalize(path);
        if (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':' &&
            (value.Length == 2 || value[2] == '/')) return value.Replace('/', '\\').TrimEnd('\\') + (value.Length == 2 ? "\\" : "");
        if (value.StartsWith("//", StringComparison.Ordinal)) return value.Replace('/', '\\');
        // An absolute Unix path is a logical remote path on Windows too.
        return value.StartsWith('/') ? value : Path.GetFullPath(path);
    }
}
