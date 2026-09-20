using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Picer.App;

internal static class MediaAgentController
{
    private static Process? _process;
    private static readonly string ConfigPath = Path.Combine(AppPaths.DataRoot, "media-agent.json");

    public sealed record Settings(bool Enabled, string ServiceIp);

    public static Settings Load()
    {
        try
        {
            if (File.Exists(ConfigPath) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath)) is { } settings) return settings;
        }
        catch { }
        return new Settings(false, "");
    }

    public static void Save(Settings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool Start(string serviceIp, string token, string? spkiPin, IEnumerable<string> roots)
    {
        Stop();
        var executable = FindExecutable();
        if (executable is null) return false;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        info.Environment["PICER_INDEX_SERVICE_IP"] = serviceIp;
        info.Environment["PICER_INDEX_SERVICE_TOKEN"] = token;
        if (!string.IsNullOrWhiteSpace(spkiPin)) info.Environment["PICER_INDEX_SERVICE_SPKI_PIN"] = spkiPin;
        info.Environment["PICER_MEDIA_AGENT_ROOTS"] = string.Join(';', roots.Where(Directory.Exists));
        _process = Process.Start(info);
        return _process is not null;
    }

    public static void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
        _process = null;
    }

    private static string? FindExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Picer.MediaAgent.exe"),
            Path.Combine(AppContext.BaseDirectory, "media-agent", "Picer.MediaAgent.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Picer.MediaAgent", "bin", "Debug", "net8.0-windows", "Picer.MediaAgent.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Picer.MediaAgent", "bin", "Release", "net8.0-windows", "win-x64", "Picer.MediaAgent.exe"))
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
