using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Indexing;

namespace Picer.IndexService.Identity;

public sealed record LegacyMigrationReport(string AccountId, string SpaceId, string BackupDirectory, IReadOnlyDictionary<string, long> Counts);

/// <summary>Offline migration only: a complete closed-file backup precedes staging, verification and atomic publication.</summary>
public sealed class LegacySpaceMigrator(AccountStore accounts, RestoreGuard guard)
{
    private sealed record Journal(string Source, string AccountId, string SpaceId, string Operation, string Backup, string State,
        string? DatabaseHash = null, Dictionary<string, long>? Counts = null);

    public LegacyMigrationReport Migrate(string legacyDatabase, string ownerLogin, string ownerPassword, string spaceName)
    {
        legacyDatabase = Path.GetFullPath(legacyDatabase);
        var migrationRoot = Path.Combine(accounts.Root, "legacy-migration"); Directory.CreateDirectory(migrationRoot);
        using var maintenance = new AccountRuntimeLease(accounts.Root);
        accounts.InitializeSpaceGuard = guard.InitializeNewSpace;
        var owner = accounts.GetLegacyMigration();
        if (owner is null)
        {
            _ = accounts.CreateLegacyOwner(ownerLogin, ownerPassword, spaceName);
            owner = accounts.GetLegacyMigration() ?? throw new InvalidDataException("legacy_owner_not_created");
        }
        var journalPath = Path.Combine(migrationRoot, "migration.json");
        Journal? journal = File.Exists(journalPath) ? JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(journalPath)) : null;
        if (journal is not null && (journal.Source != legacyDatabase || journal.AccountId != owner.AccountId || journal.SpaceId != owner.SpaceId))
            throw new InvalidOperationException("legacy_migration_inputs_changed");
        if (owner.State == "complete")
            return journal is not null && journal.Counts is not null
                ? new(owner.AccountId, owner.SpaceId, Path.Combine(migrationRoot, journal.Backup), journal.Counts)
                : throw new InvalidDataException("legacy_migration_manifest_missing");
        if (journal is null)
        {
            journal = new(legacyDatabase, owner.AccountId, owner.SpaceId, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "preparing");
            Save(journalPath, journal);
        }
        foreach (var id in new[] { journal.Operation, journal.Backup, journal.SpaceId })
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("invalid_migration_path");
        var backupDirectory = Path.Combine(migrationRoot, journal.Backup);
        var stagingDirectory = Path.Combine(migrationRoot, journal.Operation);
        var finalDirectory = Path.Combine(accounts.Root, "spaces", journal.SpaceId);
        if (journal.State == "preparing")
        {
            // Interrupted, incomplete copies are retained for diagnosis, never treated as a valid backup.
            if (Directory.Exists(backupDirectory))
            {
                journal = journal with { Backup = Guid.NewGuid().ToString("N") };
                Save(journalPath, journal); backupDirectory = Path.Combine(migrationRoot, journal.Backup);
            }
            ClosedDatabaseBackup.Create(legacyDatabase, Path.Combine(backupDirectory, "picer.db"));
            journal = journal with { State = "backed-up" }; Save(journalPath, journal);
        }
        if (journal.State == "backed-up")
        {
            if (Directory.Exists(stagingDirectory))
            {
                journal = journal with { Operation = Guid.NewGuid().ToString("N") };
                Save(journalPath, journal); stagingDirectory = Path.Combine(migrationRoot, journal.Operation);
            }
            Directory.CreateDirectory(stagingDirectory);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var source = Path.Combine(backupDirectory, "picer.db" + suffix);
                if (File.Exists(source)) File.Copy(source, Path.Combine(stagingDirectory, "picer.db" + suffix));
            }
            var staged = Path.Combine(stagingDirectory, "picer.db");
            var before = Counts(staged);
            using (var database = new PicerDatabase(staged))
            {
                NamespaceSources(database, journal.SpaceId);
                using (var check = database.Command("PRAGMA quick_check"))
                    if (check.ExecuteScalar() as string != "ok") throw new InvalidDataException("legacy_database_integrity_failed");
                using (var foreign = database.Command("PRAGMA foreign_key_check"))
                using (var row = foreign.ExecuteReader()) if (row.Read()) throw new InvalidDataException("legacy_foreign_keys_failed");
            }
            var after = Counts(staged);
            foreach (var (table, count) in before)
                if (!after.TryGetValue(table, out var actual) || actual != count)
                    throw new InvalidDataException("legacy_table_count_changed:" + table);
            using (var database = new PicerDatabase(staged))
            {
                guard.VerifyCurrent(journal.SpaceId, 0);
                using var checkpoint = database.Command("PRAGMA wal_checkpoint(TRUNCATE)");
                using var row = checkpoint.ExecuteReader();
                if (!row.Read() || row.GetInt64(0) != 0) throw new IOException("legacy_checkpoint_busy");
            }
            journal = journal with { State = "verified", DatabaseHash = Hash(staged), Counts = before };
            Save(journalPath, journal);
        }
        if (journal.State == "verified")
        {
            if (!Directory.Exists(finalDirectory))
            {
                if (Hash(Path.Combine(stagingDirectory, "picer.db")) != journal.DatabaseHash)
                    throw new InvalidDataException("legacy_staging_hash_changed");
                Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
                Directory.Move(stagingDirectory, finalDirectory);
            }
            if (Hash(Path.Combine(finalDirectory, "picer.db")) != journal.DatabaseHash)
                throw new InvalidDataException("legacy_published_hash_changed");
            // Unknown sources remain unbound; no machine name, old bearer token or matching drive letter can claim them.
            accounts.PublishLegacySpace(owner);
            journal = journal with { State = "complete" }; Save(journalPath, journal);
        }
        if (journal.State != "complete") throw new InvalidDataException("legacy_migration_state_invalid");
        return new(owner.AccountId, owner.SpaceId, backupDirectory, journal.Counts ?? throw new InvalidDataException("legacy_counts_missing"));
    }

    private static void NamespaceSources(PicerDatabase database, string spaceId)
    {
        using var transaction = database.BeginTransaction();
        using var libraryQuery = database.Command("SELECT library_id FROM library_identity WHERE id=1");
        var library = (string)libraryQuery.ExecuteScalar()!;
        using var epochQuery = database.Command("SELECT epoch FROM sync_space_state WHERE id=1");
        var epoch = (string)epochQuery.ExecuteScalar()!;
        foreach (var source in database.GetSources())
        {
            var publisher = Guid.NewGuid().ToString("N");
            var originalRoot = MediaSourcePath.ForStorage(source.Path);
            using (var record = database.Command("""
                INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
                    VALUES($publisher,$library,$publisher,'legacy-unbound',$space,'legacy-unbound',$epoch);
                INSERT INTO sync_source_bindings(publisher_id,local_source_id,source_id,source_uuid,original_path)
                    VALUES($publisher,$source,$source,$uuid,$original);
                UPDATE sources SET path=$path WHERE id=$source;
                """))
            {
                record.Parameters.AddWithValue("$publisher", publisher); record.Parameters.AddWithValue("$library", library);
                record.Parameters.AddWithValue("$space", spaceId);
                record.Parameters.AddWithValue("$epoch", epoch); record.Parameters.AddWithValue("$source", source.Id);
                record.Parameters.AddWithValue("$uuid", Guid.NewGuid().ToString("N")); record.Parameters.AddWithValue("$original", originalRoot);
                record.Parameters.AddWithValue("$path", PublishedMediaPath.Qualify(publisher, originalRoot)); record.ExecuteNonQuery();
            }
            foreach (var table in new[] { "images", "videos" })
            {
                var rows = new List<(long Id, string Path)>();
                using (var query = database.Command($"SELECT id,path FROM {table} WHERE source_id=$source"))
                {
                    query.Parameters.AddWithValue("$source", source.Id);
                    using var reader = query.ExecuteReader(); while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1)));
                }
                foreach (var row in rows)
                {
                    using var update = database.Command($"UPDATE {table} SET path=$path WHERE id=$id");
                    update.Parameters.AddWithValue("$id", row.Id);
                    update.Parameters.AddWithValue("$path", PublishedMediaPath.Qualify(publisher, MediaSourcePath.ForStorage(row.Path)));
                    update.ExecuteNonQuery();
                }
            }
        }
        transaction.Commit();
    }

    private static Dictionary<string, long> Counts(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();
        var names = new List<string>();
        using (var query = connection.CreateCommand())
        {
            // FTS shadow pages are implementation details: rebuilding a token changes their row count.
            query.CommandText = "SELECT name FROM pragma_table_list WHERE schema='main' AND type IN ('table','virtual') AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'sync_%' AND name NOT LIKE 'mirror_%' AND name<>'library_identity'";
            using var reader = query.ExecuteReader(); while (reader.Read()) names.Add(reader.GetString(0));
        }
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in names)
        {
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            counts.Add(table, Convert.ToInt64(query.ExecuteScalar()));
        }
        return counts;
    }

    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void Save(string path, Journal journal)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(JsonSerializer.SerializeToUtf8Bytes(journal)); output.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
