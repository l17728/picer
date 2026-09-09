using System.IO;
using System.Text.Json;

namespace Picer.Core;

public static class PicerPaths
{
    private const string ConfigFileName = "picer.paths.json";
    private const string RepoMarkerFileName = "picer.sln";
    private static string? _repoRoot;

    public static string RepoRoot => _repoRoot ??= DiscoverRepoRoot();

    public static string Resolve(string key, string relativeDefault)
    {
        var value = ReadConfigValue(key) is { Length: > 0 } configured ? configured : relativeDefault;
        return Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(Path.Combine(RepoRoot, value));
    }

    private static string? ReadConfigValue(string key)
    {
        try
        {
            var path = Path.Combine(RepoRoot, ConfigFileName);
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(key, out var element)
                && element.ValueKind == JsonValueKind.String
                && element.GetString() is { Length: > 0 } value
                ? value : null;
        }
        catch { return null; }
    }

    private static string DiscoverRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, RepoMarkerFileName)))
                return directory.FullName;
        return AppContext.BaseDirectory;
    }
}
