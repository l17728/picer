using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Picer.Core.Data;
using Picer.Core.Storage;
using Picer.Core.Thumbnails;

namespace Picer.App;

public sealed record DiagnosticReport(DateTime CreatedUtc, string AppVersion, string Os, string Architecture, string DotNet,
    int DatabaseVersion, string DatabaseIntegrity, IReadOnlyDictionary<string, long> Counts, string StorageScheme,
    IReadOnlyList<DiagnosticComponent> Components, IReadOnlyDictionary<string, string> LocalAiSettings);
public sealed record DiagnosticComponent(string Name, bool Ready, string State, long Bytes, string License);
public sealed record CacheArea(string Id, string Name, string Path, long Bytes, bool CanClear, string Note);

public sealed partial class DiagnosticService(PicerDatabase database, IStorageProvider storage, ThumbnailService thumbnails, string runtimeRoot, string dataRoot)
{
    private readonly string _dataRoot = Path.GetFullPath(dataRoot);
    public async Task<DiagnosticReport> BuildReportAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report("正在校验运行组件……");
        var runtime = await new RuntimeComponentService(runtimeRoot).VerifyAsync(cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); progress?.Report("正在检查数据库……");
        using var connection = database.OpenReadConnection();
        int version; string integrity;
        using (var command = connection.CreateCommand()) { command.CommandText = "PRAGMA user_version"; version = Convert.ToInt32(command.ExecuteScalar()); }
        using (var command = connection.CreateCommand()) { command.CommandText = "PRAGMA quick_check"; integrity = Convert.ToString(command.ExecuteScalar()) ?? "unknown"; }
        var counts = new Dictionary<string, long>();
        foreach (var item in new[] { ("images", "images"), ("people", "people"), ("faces", "detected_faces"), ("clipVectors", "clip_image_embeddings"), ("florenceResults", "florence_image_results"), ("sceneDescriptions", "scene_group_descriptions"), ("videos", "videos") })
        {
            using var command = connection.CreateCommand(); command.CommandText = $"SELECT COUNT(*) FROM {item.Item2}"; counts[item.Item1] = Convert.ToInt64(command.ExecuteScalar());
        }
        var settings = new Dictionary<string, string>();
        AddSafeSetting(settings, AppPaths.FlorenceRuntimeSettingsPath, "FlorenceProvider", "provider");
        settings["ChineseClip"] = File.Exists(AppPaths.ChineseClipModelPath) ? "bundled" : "missing";
        return new(DateTime.UtcNow, typeof(DiagnosticService).Assembly.GetName().Version?.ToString() ?? "unknown",
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), Environment.Version.ToString(), version, integrity,
            counts, storage.Scheme, runtime.Select(x => new DiagnosticComponent(x.Spec.Name, x.Ready, x.State, x.Spec.Bytes, x.Spec.License)).ToArray(), settings);
    }

    public async Task ExportJsonAsync(string path, CancellationToken cancellationToken = default) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(await BuildReportAsync(cancellationToken: cancellationToken), new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    public async Task ExportCsvAsync(string path, CancellationToken cancellationToken = default)
    {
        var report = await BuildReportAsync(cancellationToken: cancellationToken); var rows = new List<(string, string)> { ("createdUtc", report.CreatedUtc.ToString("O")), ("appVersion", report.AppVersion), ("os", report.Os), ("architecture", report.Architecture), ("dotnet", report.DotNet), ("databaseVersion", report.DatabaseVersion.ToString()), ("databaseIntegrity", report.DatabaseIntegrity), ("storageScheme", report.StorageScheme) };
        rows.AddRange(report.Counts.Select(x => ($"count.{x.Key}", x.Value.ToString()))); rows.AddRange(report.Components.Select(x => ($"component.{x.Name}", x.Ready ? "ready" : x.State)));
        await File.WriteAllLinesAsync(path, new[] { "key,value" }.Concat(rows.Select(x => $"{Csv(x.Item1)},{Csv(x.Item2)}")), cancellationToken);
    }
    public async Task CreateDiagnosticPackageAsync(string path, CancellationToken cancellationToken = default)
    {
        var temporary = path + ".partial"; if (File.Exists(temporary)) File.Delete(temporary);
        using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            var report = archive.CreateEntry("diagnostic-report.json", CompressionLevel.Optimal); await using (var stream = report.Open()) await JsonSerializer.SerializeAsync(stream, await BuildReportAsync(cancellationToken: cancellationToken), cancellationToken: cancellationToken);
            var logs = archive.CreateEntry("recent-logs-redacted.txt", CompressionLevel.Optimal); await using var output = new StreamWriter(logs.Open(), Encoding.UTF8);
            foreach (var file in Directory.Exists(Path.Combine(_dataRoot, "logs")) ? Directory.EnumerateFiles(Path.Combine(_dataRoot, "logs"), "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(3) : [])
            {
                cancellationToken.ThrowIfCancellationRequested(); var text = await ReadTailAsync(file, 256 * 1024, cancellationToken); await output.WriteLineAsync(Redact(text));
            }
        }
        File.Move(temporary, path, true);
    }

    public IReadOnlyList<CacheArea> GetCaches() =>
    [
        Area("thumbs", "图片缩略图", thumbnails.CacheDir, true, "可自动重新生成"),
        Area("faces", "人脸缩略图", AppPaths.FaceThumbnailDir, true, "可从检测框重新生成"),
        Area("remote", "远程读取缓存", AppServices.Config.Cache?.Directory ?? Path.Combine(_dataRoot, "cache"), storage is CachedStorageProvider, storage is CachedStorageProvider ? "可按需重新下载" : "当前未启用远程缓存"),
        Area("video", "视频关键帧", Path.Combine(_dataRoot, "video-keyframes"), false, "与视频索引关联，请通过后续维护任务清理"),
        Area("models", "随包模型", AppPaths.RuntimeRoot, false, "发布运行组件，不允许清理")
    ];
    public void ClearCache(string id)
    {
        if (id == "remote" && storage is CachedStorageProvider cached) { cached.ClearCache(); return; }
        var target = id switch { "thumbs" => thumbnails.CacheDir, "faces" => AppPaths.FaceThumbnailDir, _ => throw new InvalidOperationException("该区域不允许清理。") };
        var resolved = Path.GetFullPath(target); var root = _dataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("缓存目录不在 Picer 数据目录内。");
        if (Directory.Exists(resolved)) foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) try { File.Delete(file); } catch { }
        if (id == "thumbs") using (var command = database.Command("UPDATE images SET thumb_path=NULL")) command.ExecuteNonQuery();
    }
    public static string Redact(string text) { var value = WindowsPathRegex().Replace(text, "[PATH]"); value = SecretRegex().Replace(value, "$1=[REDACTED]"); return value; }
    private static CacheArea Area(string id, string name, string path, bool clear, string note) => new(id, name, path, Size(path), clear, note);
    private static long Size(string path) { try { return File.Exists(path) ? new FileInfo(path).Length : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length) : 0; } catch { return 0; } }
    private static void AddSafeSetting(IDictionary<string, string> target, string path, string label, string property) { try { using var doc = JsonDocument.Parse(File.ReadAllText(path)); if (doc.RootElement.TryGetProperty(property, out var value)) target[label] = value.GetString() ?? "unknown"; } catch { target[label] = "not-validated"; } }
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static async Task<string> ReadTailAsync(string path, int maximumBytes, CancellationToken token) { await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); stream.Seek(Math.Max(0, stream.Length - maximumBytes), SeekOrigin.Begin); using var reader = new StreamReader(stream); return await reader.ReadToEndAsync(token); }
    [GeneratedRegex("(?i)(?:[A-Z]:\\\\|\\\\\\\\)[^\\s\\\"']+")] private static partial Regex WindowsPathRegex();
    [GeneratedRegex(@"(?i)\b(token|password|api[_-]?key|authorization)\s*[:=]\s*[^\s,;]+") ] private static partial Regex SecretRegex();
}
