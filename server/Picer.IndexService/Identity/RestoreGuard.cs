using Microsoft.Data.Sqlite;

namespace Picer.IndexService.Identity;

/// <summary>Stored outside the versioned service backup tree. Contains only space generations, never media identifiers.</summary>
public sealed class RestoreGuard
{
    private readonly string _connectionString;
    private readonly string _serviceId;
    public RestoreGuard(string directory, string serviceId, bool allowCreate)
    {
        directory = Path.GetFullPath(directory);
        var path = Path.Combine(directory, "restore-guard.db");
        if (!File.Exists(path) && !allowCreate) throw new InvalidDataException("restore_guard_missing_service_quarantined");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _serviceId = serviceId;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > 1 || version == 0 && !allowCreate) throw new InvalidDataException("restore_guard_schema_invalid");
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS guard_identity(id INTEGER PRIMARY KEY CHECK(id=1),service_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS space_generations(space_id TEXT PRIMARY KEY,generation INTEGER NOT NULL,pending INTEGER NOT NULL DEFAULT 0);
            INSERT OR IGNORE INTO guard_identity VALUES(1,$service);
            PRAGMA user_version=1;
            """;
        command.Parameters.AddWithValue("$service", serviceId); command.ExecuteNonQuery();
        command.CommandText = "SELECT service_id FROM guard_identity WHERE id=1";
        if (command.ExecuteScalar() as string != serviceId) throw new InvalidDataException("restore_guard_identity_mismatch");
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=EXTRA; PRAGMA busy_timeout=5000;"; command.ExecuteNonQuery();
        return connection;
    }

    public void InitializeNewSpace(string spaceId)
    {
        if (!Guid.TryParseExact(spaceId, "N", out _)) throw new ArgumentException("invalid_space_id");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        AccountStore.Execute(connection, transaction, "INSERT INTO space_generations(space_id,generation,pending) VALUES($space,0,0)", ("$space", spaceId));
        transaction.Commit();
    }

    public (long Generation, bool Pending) Read(string spaceId)
    {
        using var connection = Open();
        using var command = AccountStore.Command(connection, null,
            "SELECT generation,pending FROM space_generations WHERE space_id=$space AND (SELECT service_id FROM guard_identity WHERE id=1)=$service",
            ("$space", spaceId), ("$service", _serviceId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("restore_guard_space_missing");
        return (reader.GetInt64(0), reader.GetInt64(1) != 0);
    }

    public long PreparePurge(string spaceId, long currentGeneration)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        if (AccountStore.Execute(connection, transaction,
            "UPDATE space_generations SET generation=generation+1,pending=1 WHERE space_id=$space AND generation=$current AND pending=0",
            ("$space", spaceId), ("$current", currentGeneration)) != 1) throw new InvalidDataException("restore_guard_generation_conflict");
        transaction.Commit();
        return checked(currentGeneration + 1);
    }

    public void CompletePurge(string spaceId, long generation)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        if (AccountStore.Execute(connection, transaction, "UPDATE space_generations SET pending=0 WHERE space_id=$space AND generation=$generation",
            ("$space", spaceId), ("$generation", generation)) != 1) throw new InvalidDataException("restore_guard_generation_conflict");
        transaction.Commit();
    }

    public void VerifyCurrent(string spaceId, long databaseGeneration)
    {
        var guard = Read(spaceId);
        if (guard.Generation != databaseGeneration || guard.Pending) throw new InvalidDataException("restored_database_requires_recovery");
    }
}
