using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

/// <summary>Remote metadata application must not schedule a full metadata upload back to its origin.</summary>
internal static class SpaceMetadataSuppressionMigration
{
    public static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name,tbl_name FROM sqlite_master WHERE type='trigger' AND name GLOB 'mirror_metadata_*'";
        var triggers = new List<(string Name, string Table)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) triggers.Add((reader.GetString(0), reader.GetString(1)));
        foreach (var (name, table) in triggers)
        {
            var operation = new[] { "INSERT", "UPDATE", "DELETE" }.Single(value => name.EndsWith("_" + value, StringComparison.Ordinal));
            var quotedName = name.Replace("\"", "\"\"", StringComparison.Ordinal);
            var quotedTable = table.Replace("\"", "\"\"", StringComparison.Ordinal);
            command.CommandText = $"""
                DROP TRIGGER "{quotedName}";
                CREATE TRIGGER "{quotedName}" AFTER {operation} ON "{quotedTable}"
                WHEN NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1)
                BEGIN UPDATE mirror_revision SET metadata_revision=metadata_revision+1 WHERE id=1; END;
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText = "PRAGMA user_version=57";
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
