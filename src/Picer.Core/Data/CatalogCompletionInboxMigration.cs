using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class CatalogCompletionInboxMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS sync_catalog_completion_inbox(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                operation_id TEXT NOT NULL,epoch TEXT NOT NULL,kind TEXT NOT NULL,global_id TEXT NOT NULL,
                media_count INTEGER NOT NULL CHECK(media_count>=0),state TEXT NOT NULL DEFAULT 'pending',
                remote_completed INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(publisher_id,operation_id));
            CREATE INDEX IF NOT EXISTS idx_catalog_completion_inbox_epoch
                ON sync_catalog_completion_inbox(publisher_id,epoch,operation_id);
            PRAGMA user_version=83;
            """;
        command.ExecuteNonQuery();transaction.Commit();
    }
}
