namespace Picer.Core.Data;

/// <summary>Copies the complete main/WAL/SHM set only after every managed lease and SQLite file lock is exclusively held.</summary>
public static class ClosedDatabaseBackup
{
    public static void Create(string source, string destination) => DatabaseMigrationSafety.CopyClosed(source, destination);
}
