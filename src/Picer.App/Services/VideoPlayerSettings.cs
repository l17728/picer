using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Picer.App.Infrastructure;
using Picer.Core.Gateway;

namespace Picer.App.Services;

public sealed record VideoPlayerSettings(string Executable = "", VideoPlaybackMode Mode = VideoPlaybackMode.Automatic, int Connections = 2, bool AskEveryTime = false)
{
    private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "video-player.json");
    public static bool IsConfigured => File.Exists(SettingsPath);
    public static VideoPlayerSettings Load()
    {
        try
        {
            var result = JsonSerializer.Deserialize<VideoPlayerSettings>(File.ReadAllText(SettingsPath));
            return result is not null && Enum.IsDefined(result.Mode) && result.Connections is >= 2 and <= 4 ? result : new();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, SettingsPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public string ResolveExecutable(string extension)
    {
        if (!string.IsNullOrWhiteSpace(Executable)) return Path.GetFullPath(Executable);
        var buffer = new StringBuilder(32768);
        uint count = (uint)buffer.Capacity;
        return AssocQueryString(0, 2, extension, "open", buffer, ref count) == 0 && File.Exists(buffer.ToString()) ? buffer.ToString() : "";
    }
    public static VideoPlayerKind Identify(string executable)
    {
        try { return VideoPlayerPolicy.Identify(executable, FileVersionInfo.GetVersionInfo(executable).ProductName); }
        catch (Exception exception) when (exception is IOException or ArgumentException) { return VideoPlayerKind.Unknown; }
    }
    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint AssocQueryString(uint flags, uint kind, string association, string extra, StringBuilder output, ref uint count);
}
