using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Picer.App;

internal static class MediaAgentController
{
    private static Process? _process;
    private static readonly string ConfigPath = Path.Combine(AppPaths.DataRoot, "media-agent.json");

    public sealed record Settings(bool Enabled, string ServiceIp);

    private static string RepairRootsPath => Path.Combine(AppPaths.DataRoot, "index-repair-roots.json");

    internal static string[] LoadRepairRoots()
    {
        try { return File.Exists(RepairRootsPath) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(RepairRootsPath)) ?? [] : []; }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    internal static void AuthorizeRepairRoots(IEnumerable<string> roots)
    {
        var authorized = LoadRepairRoots().Concat(roots).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Directory.CreateDirectory(AppPaths.DataRoot);
        File.WriteAllText(RepairRootsPath, JsonSerializer.Serialize(authorized));
    }

    public static string ResolveToken()
    {
        var environment = Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN")?.Trim();
        if (!string.IsNullOrWhiteSpace(environment)) return environment;
        return ReadEndpointValue("TokenCipher", value =>
        {
            if (value.StartsWith("plain:", StringComparison.OrdinalIgnoreCase))
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value["plain:".Length..]));
            if (value.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase))
            {
                var protectedValue = Convert.FromBase64String(value["dpapi:".Length..]);
                return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedValue, null, DataProtectionScope.CurrentUser));
            }
            // Backward compatibility: early endpoint files stored the DPAPI
            // payload as a bare Base64 string without a format prefix.
            try
            {
                var protectedValue = Convert.FromBase64String(value);
                return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedValue, null, DataProtectionScope.CurrentUser));
            }
            catch (FormatException) { return null; }
            catch (CryptographicException) { return null; }
        }) ?? "";
    }

    public static string? ResolveSpkiPin()
        => Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_SPKI_PIN")?.Trim()
            ?? ReadEndpointValue("SpkiPin", value => value);

    private static string? ReadEndpointValue(string propertyName, Func<string, string?> transform)
    {
        try
        {
            if (!File.Exists(AppPaths.ServiceEndpointPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(AppPaths.ServiceEndpointPath));
            return document.RootElement.TryGetProperty(propertyName, out var property)
                ? transform(property.GetString() ?? "") : null;
        }
        catch { return null; }
    }

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

    public static bool Start(string serviceIp, string token, string? spkiPin, IEnumerable<string> roots, string? deviceCredentialPath = null)
    {
        var executable = FindExecutable();
        if (executable is null)
        {
            Picer.Core.Logging.AppLogger.Warn("MediaAgent 启动失败：未找到完整代理程序，保留现有代理进程");
            return false;
        }
        Stop();
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        info.Environment["PICER_INDEX_SERVICE_IP"] = serviceIp;
        info.Environment["PICER_INDEX_SERVICE_TOKEN"] = token;
        if (deviceCredentialPath is not null) info.Environment["PICER_AGENT_DEVICE_CREDENTIAL_PATH"] = deviceCredentialPath;
        else info.Environment.Remove("PICER_AGENT_DEVICE_CREDENTIAL_PATH");
        if (!string.IsNullOrWhiteSpace(spkiPin)) info.Environment["PICER_INDEX_SERVICE_SPKI_PIN"] = spkiPin;
        // Historical keyframes/crops are generated assets, not source files.
        // Allow these specific caches, never the entire application-data root
        // (which also contains credentials and databases).
        var generatedRoots = new[] { "thumbs", "face-thumbs", "video-keyframes", "video-summary-cache" }
            .Select(directory => Path.Combine(AppPaths.DataRoot, directory));
        info.Environment["PICER_MEDIA_AGENT_ROOTS"] = string.Join(';', (deviceCredentialPath is null ? roots.Concat(LoadRepairRoots()).Concat(generatedRoots) : roots)
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase));
        _process = Process.Start(info);
        if (_process is null) return false;
        if (_process.WaitForExit(2000))
        {
            Picer.Core.Logging.AppLogger.Warn($"MediaAgent 启动后退出；exitCode={_process.ExitCode}");
            _process.Dispose();
            _process = null;
            return false;
        }
        return !_process.HasExited;
    }

    public static void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
        _process = null;

        // A previous GUI process (or an older launch path) may have started
        // the singleton agent with a stale allow-list. Merely starting a new
        // instance is not enough: the global mutex makes it exit while the old
        // process keeps serving with obsolete roots. Restart only instances of
        // this exact bundled executable so the next Start applies fresh roots.
        var expectedExecutables = FindExecutables()
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expectedExecutables.Count == 0) return;
        foreach (var process in Process.GetProcessesByName("Picer.MediaAgent"))
        {
            using (process)
            {
                try
                {
                    var actual = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(actual) || !expectedExecutables.Contains(Path.GetFullPath(actual)))
                        continue;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or System.NotSupportedException)
                {
                    // Process teardown is best effort; Start remains guarded by
                    // the named global mutex inside MediaAgent itself.
                }
            }
        }
    }

    private static string? FindExecutable()
        => ResolveExecutableForBaseDirectory(AppContext.BaseDirectory);

    private static IEnumerable<string> FindExecutables()
        => EnumerateExecutableCandidates(AppContext.BaseDirectory);

    internal static string? ResolveExecutableForBaseDirectory(string appBaseDirectory)
        => EnumerateExecutableCandidates(appBaseDirectory).FirstOrDefault(path =>
            File.Exists(path) && File.Exists(Path.ChangeExtension(path, ".dll")) &&
            File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")) &&
            File.Exists(Path.ChangeExtension(path, ".deps.json")));

    internal static IEnumerable<string> EnumerateExecutableCandidates(string appBaseDirectory)
    {
        var baseDirectory = Path.GetFullPath(appBaseDirectory);
        yield return Path.Combine(baseDirectory, "media-agent", "Picer.MediaAgent.exe");
        yield return Path.Combine(baseDirectory, "Picer.MediaAgent.exe");

        // Development builds and portable app copies can live several levels
        // below the repository root. Walk ancestors and look for the canonical
        // src/Picer.MediaAgent layout rather than assuming a fixed number of
        // parent traversals (which silently missed the executable for packaged
        // test/portable GUI directories).
        for (var ancestor = new DirectoryInfo(baseDirectory); ancestor is not null; ancestor = ancestor.Parent)
        {
            var projectOutput = Path.Combine(ancestor.FullName, "src", "Picer.MediaAgent", "bin");
            yield return Path.Combine(projectOutput, "Debug", "net8.0-windows", "Picer.MediaAgent.exe");
            yield return Path.Combine(projectOutput, "Release", "net8.0-windows", "win-x64", "Picer.MediaAgent.exe");

            // Retain compatibility with older layouts which placed the project
            // directory directly beside the repository root.
            var legacyOutput = Path.Combine(ancestor.FullName, "Picer.MediaAgent", "bin");
            yield return Path.Combine(legacyOutput, "Debug", "net8.0-windows", "Picer.MediaAgent.exe");
            yield return Path.Combine(legacyOutput, "Release", "net8.0-windows", "win-x64", "Picer.MediaAgent.exe");
        }
    }
}
