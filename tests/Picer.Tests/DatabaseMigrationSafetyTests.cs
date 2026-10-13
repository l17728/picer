using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using System.Text.Json;

namespace Picer.Tests;

public sealed class DatabaseMigrationSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-migration-safety-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_root, "test.db");

    public DatabaseMigrationSafetyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void InMemoryDatabaseInitializesWithoutFilesystemMigrationAndRejectsFakeIndependentCopy()
    {
        using var database = new PicerDatabase(":memory:");
        var source = database.AddSource(_root);
        Assert.Equal(source.Id, Assert.Single(database.GetSources()).Id);
        using var version = database.Command("PRAGMA user_version");
        Assert.Equal((long)PicerDatabase.CurrentSchemaVersion, version.ExecuteScalar());
        using var integrity = database.Command("PRAGMA quick_check");
        Assert.Equal("ok", integrity.ExecuteScalar());
        Assert.Throws<NotSupportedException>(() => database.OpenIndependentConnection());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void FailedStagingMigrationLeavesOriginalBytesAndClosedFileBackupIntact()
    {
        CreateLegacy();
        var before = File.ReadAllBytes(DatabasePath);
        Assert.Throws<InvalidOperationException>(() => DatabaseMigrationSafety.Prepare(DatabasePath, 2, stage =>
        {
            Execute(stage, "ALTER TABLE evidence ADD COLUMN partial TEXT;");
            throw new InvalidOperationException("injected_migration_failure");
        }));
        Assert.Equal(before, File.ReadAllBytes(DatabasePath));
        var backup = Assert.Single(Directory.GetDirectories(DatabasePath + ".migration-backups"));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(backup, "test.db")));
        Assert.False(File.Exists(DatabasePath + ".migration.pending.json"));
    }

    [Fact]
    public void SuccessfulStagingMigrationPublishesOnlyCompletedDatabase()
    {
        CreateLegacy();
        DatabaseMigrationSafety.Prepare(DatabasePath, 2, stage =>
            Execute(stage, "ALTER TABLE evidence ADD COLUMN completed TEXT; PRAGMA user_version=2;"));
        using var connection = Open(DatabasePath);
        using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, version.ExecuteScalar());
        using var evidence = connection.CreateCommand(); evidence.CommandText = "SELECT value FROM evidence";
        Assert.Equal("retained", evidence.ExecuteScalar());
        Assert.False(File.Exists(DatabasePath + ".migration.pending.json"));
    }

    [Fact]
    public void PendingPublicationRestoresBackupBeforeOpeningDamagedOriginal()
    {
        CreateLegacy();
        var expected = File.ReadAllBytes(DatabasePath);
        var backup = Path.Combine(DatabasePath + ".migration-backups", "interrupted");
        Directory.CreateDirectory(backup); File.Copy(DatabasePath, Path.Combine(backup, "test.db"));
        File.WriteAllBytes(DatabasePath, [1, 2, 3]);
        File.WriteAllText(DatabasePath + ".migration.pending.json", JsonSerializer.Serialize(new { BackupDirectory = backup }));
        DatabaseMigrationSafety.Prepare(DatabasePath, 1, _ => throw new InvalidOperationException("should_not_migrate"));
        Assert.Equal(expected, File.ReadAllBytes(DatabasePath));
        Assert.False(File.Exists(DatabasePath + ".migration.pending.json"));
    }

    [Fact]
    public void UpgradeRefusesLiveManagedConnectionLease()
    {
        CreateLegacy();
        using var lease = DatabaseMigrationSafety.OpenLifetimeLease(DatabasePath);
        var failure = Assert.Throws<IOException>(() => DatabaseMigrationSafety.Prepare(DatabasePath, 2,
            _ => throw new InvalidOperationException("must_not_enter")));
        Assert.Contains("requires_closed_instances", failure.Message);
        Assert.False(Directory.Exists(DatabasePath + ".migration-backups"));
    }

    [Fact]
    public void ClosedWalSnapshotIsBackedUpAndCommittedWalVersionIsRecognized()
    {
        var source = Path.Combine(_root, "wal-source.db");
        var expected = new Dictionary<string, byte[]>();
        using (var connection = Open(source))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE evidence(value TEXT); INSERT INTO evidence VALUES('wal-retained'); PRAGMA user_version=1;";
            command.ExecuteNonQuery();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                using (var original = new FileStream(source + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var buffer = new MemoryStream())
                { original.CopyTo(buffer); expected[suffix] = buffer.ToArray(); }
                File.WriteAllBytes(DatabasePath + suffix, expected[suffix]);
            }
        }
        DatabaseMigrationSafety.Prepare(DatabasePath, 2, stage => Execute(stage, "PRAGMA user_version=2;"));
        var backup = Assert.Single(Directory.GetDirectories(DatabasePath + ".migration-backups"));
        foreach (var (suffix, bytes) in expected) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(backup, "test.db") + suffix));
        using var reopened = Open(DatabasePath);
        using var evidence = reopened.CreateCommand(); evidence.CommandText = "SELECT value FROM evidence";
        Assert.Equal("wal-retained", evidence.ExecuteScalar());
    }

    [Fact]
    public void CorruptWalHeaderIsRejectedWithoutOpeningOrChangingDatabase()
    {
        CreateLegacy();
        var wal = new byte[32];
        File.WriteAllBytes(DatabasePath + "-wal", wal);
        var before = File.ReadAllBytes(DatabasePath);
        Assert.Throws<InvalidDataException>(() => DatabaseMigrationSafety.Prepare(DatabasePath, 2,
            _ => throw new InvalidOperationException("must_not_migrate")));
        Assert.Equal(before, File.ReadAllBytes(DatabasePath));
        Assert.Equal(wal, File.ReadAllBytes(DatabasePath + "-wal"));
        Assert.False(Directory.Exists(DatabasePath + ".migration-backups"));
    }

    [Fact]
    public void FutureSchemaIsRejectedWithoutChangingBytes()
    {
        CreateLegacy();
        Execute(DatabasePath, $"PRAGMA user_version={PicerDatabase.CurrentSchemaVersion + 1};");
        var before = File.ReadAllBytes(DatabasePath);
        Assert.Throws<InvalidDataException>(() => new PicerDatabase(DatabasePath));
        Assert.Equal(before, File.ReadAllBytes(DatabasePath));
        Assert.False(Directory.Exists(DatabasePath + ".migration-backups"));
    }

    [Fact]
    public void CurrentDatabaseAllowsIndependentConnectionsAndReleasesLeasesOnDispose()
    {
        using (var first = new PicerDatabase(DatabasePath))
        using (var second = first.OpenIndependentConnection())
            Assert.Empty(second.GetSources());
        using var exclusive = new FileStream(DatabasePath + ".access", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private void CreateLegacy() => Execute(DatabasePath,
        "CREATE TABLE evidence(value TEXT); INSERT INTO evidence VALUES('retained'); PRAGMA user_version=1;");

    [Fact]
    public void ClosedMigrationBackupPreservesOriginalAndRejectsAnOpenWriterLease()
    {
        CreateLegacy();
        var before = File.ReadAllBytes(DatabasePath);
        var copy = Path.Combine(_root, "backup", "picer.db");
        using (var lease = DatabaseMigrationSafety.OpenLifetimeLease(DatabasePath))
            Assert.Throws<IOException>(() => ClosedDatabaseBackup.Create(DatabasePath, copy));
        Assert.False(File.Exists(copy));
        ClosedDatabaseBackup.Create(DatabasePath, copy);
        Assert.Equal(before, File.ReadAllBytes(copy));
        Assert.Equal(before, File.ReadAllBytes(DatabasePath));
        Assert.Throws<IOException>(() => ClosedDatabaseBackup.Create(DatabasePath, copy));
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); return connection;
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path); using var command = connection.CreateCommand();
        command.CommandText = sql; command.ExecuteNonQuery();
    }

    public void Dispose() => Directory.Delete(_root, true);
}
