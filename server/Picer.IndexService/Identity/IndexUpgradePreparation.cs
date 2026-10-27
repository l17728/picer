using System.Text.Json;
using Picer.Core.Data;

namespace Picer.IndexService.Identity;

/// <summary>Upgrade all existing space indexes before opening the public listener.</summary>
public static class IndexUpgradePreparation
{
    public static int Prepare(string root)
    {
        root = Path.GetFullPath(root);
        RejectLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException();
        var accounts = Path.Combine(root, "accounts-v2");
        RejectLinks(accounts);
        using var lease = Directory.Exists(accounts) ? new AccountRuntimeLease(accounts) : null;
        var paths = new List<string>();
        var legacy = Path.Combine(root, "picer.db");
        if (File.Exists(legacy)) paths.Add(legacy);
        var spaces = Path.Combine(accounts, "spaces");
        RejectLinks(spaces);
        if (Directory.Exists(spaces))
            foreach (var directory in Directory.EnumerateDirectories(spaces).Order(StringComparer.Ordinal))
            {
                RejectLinks(directory);
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                    throw new InvalidDataException("unexpected_space_directory");
                var path = Path.Combine(directory, "picer.db");
                if (File.Exists(path)) paths.Add(path);
            }
        if (paths.Count == 0) throw new InvalidDataException("no_existing_indexes");
        foreach (var path in paths)
        {
            RejectLinks(path);
            foreach (var suffix in new[] { "-wal", "-shm", ".access", ".migration.pending.json" }) RejectLinks(path + suffix);
            if (File.Exists(path + ".migration.pending.json"))
                throw new InvalidDataException("unfinished_migration");
        }
        foreach (var path in paths)
        {
            using var database = new PicerDatabase(path);
            using var check = database.Command("PRAGMA quick_check");
            if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
                throw new InvalidDataException("index_integrity_failed");
        }
        return paths.Count;
    }

    public static int Run(string root)
    {
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(new { indexes = Prepare(root), schema = PicerDatabase.CurrentSchemaVersion }));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine("离线索引升级失败；保留备份并检查关闭状态、磁盘空间和迁移记录。");
            return 2;
        }
    }

    private static void RejectLinks(string path)
    {
        for (var current = new FileInfo(path); current is not null; current = current.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0 && current.Attributes != (FileAttributes)(-1))
                throw new IOException("linked_upgrade_path");
    }
}
