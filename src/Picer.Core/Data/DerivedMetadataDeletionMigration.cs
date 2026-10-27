using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class DerivedMetadataDeletionMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS sync_derived_deletion_acks(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                operation_id TEXT NOT NULL REFERENCES sync_metadata_row_journal(operation_id) ON DELETE CASCADE,
                PRIMARY KEY(publisher_id,operation_id));
            CREATE INDEX IF NOT EXISTS ix_metadata_journal_delete ON sync_metadata_row_journal(operation,table_name,sequence);
            PRAGMA user_version=87;
            """;
        command.ExecuteNonQuery();transaction.Commit();
    }
}
