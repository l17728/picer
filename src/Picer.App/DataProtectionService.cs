using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;

namespace Picer.App;

public sealed record PicerBackupManifest(int FormatVersion, int DatabaseVersion, DateTime CreatedUtc, string AppVersion, string[] Entries);
public sealed record DataAreaSize(string Name, string Path, long Bytes);

public sealed class DataProtectionService(PicerDatabase database, string dataRoot)
{
    private static readonly string[] OptionalFiles = ["storage.json", "cloud-vision.json", "directory-classification.json", "geocoding-cache.json", "geocoding-cache.json.places"];
    private static string BootstrapDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Picer", "bootstrap");
    private static string PendingRestorePath => Path.Combine(BootstrapDirectory, "pending-restore.json");
    private static string PendingMigrationPath => Path.Combine(BootstrapDirectory, "pending-migration.json");
    public string DataRoot { get; } = Path.GetFullPath(dataRoot);

    public async Task CreateBackupAsync(string archivePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"picer-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            progress?.Report("正在创建一致的数据库快照……");
            var dbSnapshot = Path.Combine(temporary, "picer.db");
            database.BackupTo(dbSnapshot);
            var entries = new List<string> { "picer.db" };
            foreach (var name in OptionalFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(DataRoot, name);
                if (!File.Exists(source)) continue;
                File.Copy(source, Path.Combine(temporary, name), true); entries.Add(name);
            }
            var manifest = new PicerBackupManifest(1, ReadDatabaseVersion(dbSnapshot), DateTime.UtcNow,
                typeof(DataProtectionService).Assembly.GetName().Version?.ToString() ?? "unknown", entries.ToArray());
            await File.WriteAllTextAsync(Path.Combine(temporary, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            var target = Path.GetFullPath(archivePath); var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            var partial = target + ".partial";
            if (File.Exists(partial)) File.Delete(partial);
            progress?.Report("正在压缩备份……");
            ZipFile.CreateFromDirectory(temporary, partial, CompressionLevel.Optimal, false);
            File.Move(partial, target, true);
            progress?.Report("备份已完成。");
        }
        finally { try { Directory.Delete(temporary, true); } catch { } }
    }

    public static PicerBackupManifest ValidateBackup(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("备份缺少 manifest.json。");
        PicerBackupManifest manifest;
        using (var stream = manifestEntry.Open()) manifest = JsonSerializer.Deserialize<PicerBackupManifest>(stream) ?? throw new InvalidDataException("备份清单无法读取。");
        if (manifest.FormatVersion != 1) throw new InvalidDataException($"不支持的备份格式：{manifest.FormatVersion}。");
        if (manifest.DatabaseVersion > 19) throw new InvalidDataException($"备份数据库版本 {manifest.DatabaseVersion} 高于当前程序支持的 v19。");
        foreach (var name in manifest.Entries)
        {
            if (name.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(name) || archive.GetEntry(name) is null) throw new InvalidDataException("备份包含无效路径或缺失文件。");
        }
        var dbEntry = archive.GetEntry("picer.db") ?? throw new InvalidDataException("备份缺少数据库。");
        var temporaryDb = Path.Combine(Path.GetTempPath(), $"picer-verify-{Guid.NewGuid():N}.db");
        try
        {
            dbEntry.ExtractToFile(temporaryDb, true);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporaryDb, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open(); using var check = connection.CreateCommand(); check.CommandText = "PRAGMA quick_check";
            if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("数据库完整性检查未通过。");
        }
        finally { try { File.Delete(temporaryDb); } catch { } }
        return manifest;
    }

    public static void ScheduleRestore(string archivePath, string dataRoot)
    {
        ValidateBackup(archivePath); Directory.CreateDirectory(BootstrapDirectory);
        File.WriteAllText(PendingRestorePath, JsonSerializer.Serialize(new PendingRestore(Path.GetFullPath(archivePath), Path.GetFullPath(dataRoot))));
    }

    public static void ApplyPendingRestoreIfAny()
    {
        if (!File.Exists(PendingRestorePath)) return;
        var pending = JsonSerializer.Deserialize<PendingRestore>(File.ReadAllText(PendingRestorePath)) ?? throw new InvalidDataException("恢复计划无效。");
        ValidateBackup(pending.ArchivePath);
        Directory.CreateDirectory(pending.DataRoot);
        var recovery = Path.Combine(pending.DataRoot, $"recovery-before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var existing = Directory.EnumerateFiles(pending.DataRoot).Where(x => Path.GetFileName(x) is "picer.db" or "storage.json" or "cloud-vision.json" or "directory-classification.json" or "geocoding-cache.json" or "geocoding-cache.json.places").ToArray();
        if (existing.Length > 0)
        {
            using var recoveryArchive = ZipFile.Open(recovery, ZipArchiveMode.Create);
            foreach (var file in existing) recoveryArchive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
        }
        using (var archive = ZipFile.OpenRead(pending.ArchivePath))
            foreach (var name in new[] { "picer.db" }.Concat(OptionalFiles))
                if (archive.GetEntry(name) is { } entry) entry.ExtractToFile(Path.Combine(pending.DataRoot, name), true);
        File.Delete(PendingRestorePath);
    }

    public static void ScheduleMigration(string sourceRoot, string destinationRoot)
    {
        var source = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("新旧数据目录相同。");
        if (destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("新目录不能与当前数据目录互相包含。");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new InvalidOperationException("目标目录必须为空，避免覆盖其他数据。");
        Directory.CreateDirectory(BootstrapDirectory);
        File.WriteAllText(PendingMigrationPath, JsonSerializer.Serialize(new PendingMigration(source, destination)));
    }

    public static void ApplyPendingMigrationIfAny()
    {
        if (!File.Exists(PendingMigrationPath)) return;
        var pending = JsonSerializer.Deserialize<PendingMigration>(File.ReadAllText(PendingMigrationPath)) ?? throw new InvalidDataException("迁移计划无效。");
        var source = Path.GetFullPath(pending.SourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(pending.DestinationRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"原数据目录不存在：{source}");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("目标目录已不再为空，迁移已安全停止。");
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.GetFullPath(Path.Combine(destination, Path.GetRelativePath(source, file)));
            if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("迁移路径越界。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
        }
        var copiedDb = Path.Combine(destination, "picer.db");
        if (File.Exists(copiedDb))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copiedDb, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); connection.Open();
            using var check = connection.CreateCommand(); check.CommandText = "PRAGMA quick_check";
            if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("迁移后的数据库完整性检查未通过。");
        }
        AppPaths.SetDataRoot(destination);
        File.Delete(PendingMigrationPath);
    }

    public IReadOnlyList<DataAreaSize> GetAreaSizes() =>
    [
        Area("数据库", Path.Combine(DataRoot, "picer.db")), Area("缩略图", Path.Combine(DataRoot, "thumbs")),
        Area("人脸缩略图", Path.Combine(DataRoot, "face-thumbs")), Area("视频缓存", Path.Combine(DataRoot, "video-keyframes")),
        Area("远程缓存", AppServices.Config.Cache?.Directory is { Length: > 0 } cache ? cache : Path.Combine(DataRoot, "cache")),
        Area("随包模型", AppPaths.RuntimeRoot)
    ];
    private static DataAreaSize Area(string name, string path) => new(name, path, File.Exists(path) ? new FileInfo(path).Length : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(x => { try { return new FileInfo(x).Length; } catch { return 0; } }) : 0);
    private static int ReadDatabaseVersion(string path) { using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version"; return Convert.ToInt32(command.ExecuteScalar()); }
    private sealed record PendingRestore(string ArchivePath, string DataRoot);
    private sealed record PendingMigration(string SourceRoot, string DestinationRoot);
}
