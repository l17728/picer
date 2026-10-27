using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class MetadataConflictReviewMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DROP VIEW IF EXISTS sync_image_owners;" + ClassificationOwnershipMigration.OwnerViewSql;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT 1 FROM pragma_table_info('metadata_edit_conflicts') WHERE name='resolution_json'";
        if (command.ExecuteScalar() is null)
        {
            command.CommandText = "ALTER TABLE metadata_edit_conflicts ADD COLUMN resolution_json TEXT";
            command.ExecuteNonQuery();
        }
        command.CommandText = "SELECT 1 FROM pragma_table_info('metadata_edit_conflicts') WHERE name='resolved_version'";
        if (command.ExecuteScalar() is null)
        {
            command.CommandText = "ALTER TABLE metadata_edit_conflicts ADD COLUMN resolved_version INTEGER";
            command.ExecuteNonQuery();
        }
        command.CommandText = "PRAGMA user_version=64"; command.ExecuteNonQuery(); transaction.Commit();
    }
}
