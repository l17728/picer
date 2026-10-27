using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class CatalogRetirementCompactionMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="SELECT 1 FROM pragma_table_info('sync_retired_catalog_identities') WHERE name='compacted'";
        if(command.ExecuteScalar() is null)
        {
            command.CommandText="ALTER TABLE sync_retired_catalog_identities ADD COLUMN compacted INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS sync_catalog_retirement_predecessors(
                publisher_id TEXT NOT NULL,kind TEXT NOT NULL,global_id TEXT NOT NULL,field_operation_id TEXT NOT NULL,
                PRIMARY KEY(publisher_id,kind,global_id,field_operation_id),
                FOREIGN KEY(publisher_id,kind,global_id) REFERENCES sync_retired_catalog_identities(publisher_id,kind,global_id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS idx_catalog_predecessor_operation ON sync_catalog_retirement_predecessors(field_operation_id);
            CREATE TRIGGER IF NOT EXISTS catalog_predecessor_delete AFTER DELETE ON metadata_edits BEGIN
                DELETE FROM sync_catalog_retirement_predecessors WHERE field_operation_id=OLD.operation_id;
            END;
            CREATE TRIGGER IF NOT EXISTS catalog_predecessor_replace AFTER UPDATE OF operation_id ON metadata_edits
                WHEN NEW.operation_id<>OLD.operation_id BEGIN
                DELETE FROM sync_catalog_retirement_predecessors WHERE field_operation_id=OLD.operation_id;
            END;
            PRAGMA user_version=85;
            """;
        command.ExecuteNonQuery();transaction.Commit();
    }
}
