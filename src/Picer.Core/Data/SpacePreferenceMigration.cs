using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class SpacePreferenceMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand(); command.Transaction=transaction;
        command.CommandText="SELECT 1 FROM pragma_table_info('metadata_edit_conflicts') WHERE name='resolved_version'";
        if (command.ExecuteScalar() is null)
        {
            command.CommandText="ALTER TABLE metadata_edit_conflicts ADD COLUMN resolved_version INTEGER";
            command.ExecuteNonQuery();
        }
        command.CommandText="SELECT automatic,manual_threshold FROM semantic_cluster_settings WHERE id=1";
        bool automatic=true; float threshold=0.82f;
        using (var row=command.ExecuteReader())
            if (row.Read()) { automatic=row.GetBoolean(0); threshold=row.GetFloat(1); }
        // Defaults are not user decisions. Preserve only an existing customization.
        if ((!automatic || threshold!=0.82f) && float.IsFinite(threshold) && threshold is >=0.70f and <=0.95f)
        {
            command.CommandText="""
                INSERT OR IGNORE INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id)
                VALUES('space-setting',1,'clustering',$value,$now,$op)
                """;
            command.Parameters.AddWithValue("$value",JsonSerializer.Serialize(new ClusterPreference(automatic,threshold)));
            command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O")); command.Parameters.AddWithValue("$op",Guid.NewGuid().ToString("N"));
            var inserted=command.ExecuteNonQuery(); command.Parameters.Clear();
            if (inserted>0)
            {
                command.CommandText="UPDATE mirror_revision SET metadata_revision=metadata_revision+1 WHERE id=1";
                command.ExecuteNonQuery();
            }
        }
        command.CommandText="DROP TRIGGER IF EXISTS space_sync_notify_cluster_preference"; command.ExecuteNonQuery();
        foreach (var operation in new[]{"INSERT","UPDATE","DELETE"})
        {
            var row=operation=="DELETE"?"OLD":"NEW";
            command.CommandText=$"""
                CREATE TRIGGER IF NOT EXISTS space_sync_notify_cluster_edit_{operation} AFTER {operation} ON metadata_edits
                WHEN {row}.kind='space-setting' AND NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1)
                BEGIN
                    UPDATE mirror_revision SET metadata_revision=metadata_revision+1 WHERE id=1;
                    INSERT INTO sync_publisher_notifications(publisher_id,revision)
                    SELECT publisher_id,1 FROM sync_publishers WHERE state='receiving'
                    ON CONFLICT(publisher_id) DO UPDATE SET revision=revision+1;
                END;
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText="PRAGMA user_version=65"; command.ExecuteNonQuery(); transaction.Commit();
    }
}
