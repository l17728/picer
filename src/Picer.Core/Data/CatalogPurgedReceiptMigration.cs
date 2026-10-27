using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class CatalogPurgedReceiptMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS sync_catalog_purged_receipt_counts(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                operation_id TEXT NOT NULL,kind TEXT NOT NULL,global_id TEXT NOT NULL,
                purged_count INTEGER NOT NULL CHECK(purged_count>0),
                PRIMARY KEY(publisher_id,operation_id));
            """;
        command.ExecuteNonQuery();
        foreach(var (table,kind) in new[]{("images","image"),("videos","video")})
        {
            // Retain only an aggregate count for an existing catalog operation.
            // No deleted media ID, hash, path, embedding or media global ID survives.
            command.CommandText=$"""
                CREATE TRIGGER IF NOT EXISTS catalog_receipt_count_purge_{kind} BEFORE DELETE ON {table} BEGIN
                    INSERT INTO sync_catalog_purged_receipt_counts(publisher_id,operation_id,kind,global_id,purged_count)
                    SELECT owned.publisher_id,owned.operation_id,owned.kind,owned.global_id,1 FROM (
                        SELECT publisher_id,operation_id,kind,global_id FROM sync_catalog_retirement_receipts
                            WHERE media_kind='{kind}' AND media_id=OLD.id
                        UNION
                        SELECT r.publisher_id,r.operation_id,r.kind,r.global_id FROM sync_catalog_retirements r
                            JOIN sync_catalog_retirement_media m ON m.operation_id=r.operation_id
                            WHERE m.media_kind='{kind}' AND m.media_id=OLD.id
                    ) owned WHERE NOT EXISTS(SELECT 1 FROM sync_retired_catalog_identities retired
                        WHERE retired.publisher_id=owned.publisher_id AND retired.kind=owned.kind AND retired.global_id=owned.global_id)
                    ON CONFLICT(publisher_id,operation_id) DO UPDATE SET purged_count=purged_count+1;
                END;
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText="PRAGMA user_version=84";command.ExecuteNonQuery();transaction.Commit();
    }
}
