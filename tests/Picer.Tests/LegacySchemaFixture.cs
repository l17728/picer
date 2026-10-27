using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using System.Text.RegularExpressions;

namespace Picer.Tests;

internal static class LegacySchemaFixture
{
    internal static void RevertPhotoClassificationIdentity(SqliteConnection connection)
    {
        using(var reset=connection.CreateCommand())
        {
            reset.CommandText="""
                DROP TRIGGER IF EXISTS photo_metadata_binding_purge;
                DROP TRIGGER IF EXISTS photo_classification_content_invalidate;
                DROP TRIGGER IF EXISTS photo_classification_field_notify_INSERT;
                DROP TRIGGER IF EXISTS photo_classification_field_notify_UPDATE;
                DROP TRIGGER IF EXISTS photo_metadata_bind_verified_hash;
                DROP TRIGGER IF EXISTS photo_metadata_file_version_insert;
                DROP TRIGGER IF EXISTS photo_metadata_file_version_update;
                DROP TABLE IF EXISTS photo_metadata_operation_bindings;
                DROP TABLE IF EXISTS sync_photo_content_identities;
                DROP TABLE IF EXISTS photo_metadata_file_versions;
                """;
            reset.ExecuteNonQuery();
        }
    }

    internal static void RevertPhotoFaceCacheOwnership(SqliteConnection connection)
    {
        RevertPhotoClassificationIdentity(connection);
        var triggers=new List<(string Name,string Sql)>();
        using(var read=connection.CreateCommand())
        {
            read.CommandText="SELECT name,sql FROM sqlite_master WHERE type='trigger' AND name LIKE 'content_%' AND sql LIKE '%sync_image_owners%'";
            using var rows=read.ExecuteReader();while(rows.Read()) triggers.Add((rows.GetString(0),rows.GetString(1)));
        }
        foreach(var (name,original) in triggers)
        {
            var sql=original.Replace(",(SELECT owner_key FROM sync_image_owners WHERE image_id=i.id)","",StringComparison.Ordinal)
                .Replace(" AND json_extract(d.item_key,'$[#-1]')=(SELECT owner_key FROM sync_image_owners WHERE image_id=new.id)","",StringComparison.Ordinal)
                .Replace(" AND (SELECT owner_key FROM sync_image_owners WHERE image_id=new.id) NOT LIKE 'ambiguous-source:%'","",StringComparison.Ordinal);
            using var command=connection.CreateCommand();
            command.CommandText="DROP TRIGGER \""+name.Replace("\"","\"\"",StringComparison.Ordinal)+"\";"+
                (name=="content_owned_face_cache_purge"?"":sql);
            command.ExecuteNonQuery();
        }
    }

    private static void RevertVideoFaceOwnership(SqliteConnection connection)
    {
        using var command=connection.CreateCommand();
        command.CommandText="SELECT sql FROM sqlite_master WHERE type='trigger' AND name='video_content_rebind'";
        if(command.ExecuteScalar() is string rebind)
        {
            rebind=Regex.Replace(rebind,@"/\* legacy_owner_transfer_start \*/.*?/\* legacy_owner_transfer_end \*/","",RegexOptions.Singleline);
            rebind=Regex.Replace(rebind,@"UPDATE video_detected_faces SET owner_key='ambiguous-content:'.*?(?=UPDATE video_detected_faces SET video_id=new.content_id,)","",RegexOptions.Singleline);
            rebind=rebind.Replace(" AND target.owner_key=video_detected_faces.owner_key","",StringComparison.Ordinal)
                .Replace(" AND target.owner_key=source.owner_key","",StringComparison.Ordinal);
            command.CommandText="DROP TRIGGER video_content_rebind;"+rebind;command.ExecuteNonQuery();
        }
        command.CommandText="DROP VIEW IF EXISTS video_effective_faces; DROP TRIGGER IF EXISTS video_face_owner_default; DROP TABLE IF EXISTS video_face_ownership_reviews;";
        command.ExecuteNonQuery();
        command.CommandText="SELECT 1 FROM pragma_table_info('video_detected_faces') WHERE name='owner_key'";
        if(command.ExecuteScalar() is null) return;
        command.CommandText="UPDATE video_detected_faces SET owner_key=''";command.ExecuteNonQuery();
    }

    // Focused older-version fixtures retain unrelated compatible tables, but
    // must remove this later migration's views/triggers before replaying it.
    internal static void RevertVideoMetadataOwnership(SqliteConnection connection)
    {
        RevertVideoFaceOwnership(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' AND (name GLOB 'space_sync_video_legacy_purge_*' OR name='space_sync_video_verify_legacy' OR name GLOB 'space_sync_legacy_owner_*' OR name GLOB 'space_sync_owned_video_edit_*')";
        var triggers = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) triggers.Add(reader.GetString(0));
        foreach (var name in triggers)
        {
            command.CommandText = "DROP TRIGGER \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            DROP TABLE IF EXISTS sync_video_content_identities;
            DROP VIEW IF EXISTS video_effective_tags;
            DROP VIEW IF EXISTS video_effective_concepts;
            DROP VIEW IF EXISTS video_owned_metadata_edits;
            DROP VIEW IF EXISTS sync_video_owners;
            DROP TABLE IF EXISTS video_metadata_ownership_reviews;
            DROP TABLE IF EXISTS video_legacy_metadata_owners;
            DROP INDEX IF EXISTS ix_metadata_edits_operation;
            PRAGMA user_version=62;
            """;
        command.ExecuteNonQuery();
    }

    internal static void RevertVersion43(SqliteConnection connection)
    {
        RevertVersion44(connection);
        var triggers = new List<(string Name, string Sql)>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='trigger' AND name IN ('content_capture_detected_faces_insert','content_capture_detected_faces_update','content_restore_image','content_restore_image_update')";
            using var reader = query.ExecuteReader();
            while (reader.Read()) triggers.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var (name, sql) in triggers)
        {
            using var replace = connection.CreateCommand();
            replace.CommandText = "DROP TRIGGER " + name + ";" + sql
                .Replace("json_object('is_archived',r.is_archived,", "json_object(", StringComparison.Ordinal)
                .Replace("detected_faces(is_archived,", "detected_faces(", StringComparison.Ordinal)
                .Replace("SELECT COALESCE(json_extract(d.payload,'$.is_archived'),0),", "SELECT ", StringComparison.Ordinal);
            replace.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE analysis_billing_events;
            DROP TABLE analysis_budget_reservations;
            DROP INDEX idx_images_source_path_nocase;
            DROP INDEX idx_videos_source_path_nocase;
            DROP INDEX idx_images_directory_path;
            DROP INDEX idx_videos_directory_path;
            ALTER TABLE detected_faces DROP COLUMN is_archived;
            UPDATE content_derivatives SET payload=json_remove(payload,'$.is_archived') WHERE kind='detected_faces';
            PRAGMA user_version=42;
            """;
        command.ExecuteNonQuery();
    }

    internal static void RevertVersion45(SqliteConnection connection)
    {
        RevertPhotoFaceCacheOwnership(connection);
        RevertVideoFaceOwnership(connection);
        // Remove the later sync protocol before changing its referenced schema.
        // SQLite validates every trigger during ALTER TABLE, including triggers on
        // unrelated media tables; leaving one behind creates an invalid fixture.
        var laterTriggers = new List<(string Name, string Table)>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT name,tbl_name FROM sqlite_master WHERE type='trigger' AND (name GLOB 'space_sync_*' OR name GLOB 'mirror_metadata_*' OR name GLOB 'catalog_*')";
            using var reader = query.ExecuteReader();
            while (reader.Read()) laterTriggers.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var (name, table) in laterTriggers)
        {
            using var resetTrigger = connection.CreateCommand();
            var quotedName = name.Replace("\"", "\"\"", StringComparison.Ordinal);
            resetTrigger.CommandText = $"DROP TRIGGER \"{quotedName}\";";
            if (name.StartsWith("mirror_metadata_", StringComparison.Ordinal))
            {
                var operation = new[] { "INSERT", "UPDATE", "DELETE" }.Single(value => name.EndsWith("_" + value, StringComparison.Ordinal));
                var quotedTable = table.Replace("\"", "\"\"", StringComparison.Ordinal);
                resetTrigger.CommandText += $"CREATE TRIGGER \"{quotedName}\" AFTER {operation} ON \"{quotedTable}\" BEGIN UPDATE mirror_revision SET metadata_revision=metadata_revision+1 WHERE id=1; END;";
            }
            resetTrigger.ExecuteNonQuery();
        }
        using (var reset = connection.CreateCommand())
        {
            reset.CommandText = """
                DROP VIEW IF EXISTS sync_image_owners;
                DROP VIEW IF EXISTS sync_active_entity_identities;
                DROP TABLE IF EXISTS sync_catalog_completion_inbox;
                DROP TABLE IF EXISTS sync_catalog_purged_receipt_counts;
                DROP TABLE IF EXISTS sync_catalog_generations;
                DROP TABLE IF EXISTS sync_catalog_retirement_predecessors;
                DROP TABLE IF EXISTS sync_catalog_completion_reviews;
                DROP TABLE IF EXISTS sync_retired_catalog_identities;
                DROP TABLE IF EXISTS sync_catalog_retirement_baselines;
                DROP TABLE IF EXISTS sync_catalog_retirement_media;
                DROP TABLE IF EXISTS sync_catalog_retirement_receipts;
                DROP TABLE IF EXISTS sync_catalog_retirements;
                DROP VIEW IF EXISTS video_effective_tags;
                DROP VIEW IF EXISTS video_effective_concepts;
                DROP VIEW IF EXISTS video_owned_metadata_edits;
                DROP VIEW IF EXISTS sync_video_owners;
                DROP TABLE IF EXISTS video_metadata_ownership_reviews;
                DROP TABLE IF EXISTS video_legacy_metadata_owners;
                DROP TABLE IF EXISTS metadata_edit_conflicts;
                DROP TABLE IF EXISTS metadata_causal_receipts;
                DROP TABLE IF EXISTS metadata_version_clock;
                DROP INDEX IF EXISTS ix_metadata_edits_server_version;
                DROP INDEX IF EXISTS ix_metadata_edits_content_field;
                DROP INDEX IF EXISTS ix_metadata_edits_version_operation;
                ALTER TABLE metadata_edits DROP COLUMN server_version;
                ALTER TABLE metadata_edits DROP COLUMN base_version;
                DROP TABLE IF EXISTS classification_rejection_ownership_reviews;
                DROP TABLE IF EXISTS sync_inspection_targets;
                DROP TABLE IF EXISTS sync_inspection_jobs;
                DROP TABLE IF EXISTS sync_source_presence;
                DROP TABLE IF EXISTS sync_publisher_notifications;
                DROP TABLE IF EXISTS sync_imported_metadata_edits;
                DROP TABLE IF EXISTS sync_derived_deletion_acks;
                DROP TABLE IF EXISTS sync_metadata_row_journal;
                DROP TABLE IF EXISTS sync_pending_identity_adoptions;
                DROP TABLE IF EXISTS sync_adopted_person_labels;
                DROP TABLE IF EXISTS sync_adoption_scan_checkpoints;
                DROP TABLE IF EXISTS sync_identity_offer_revision;
                DROP TABLE IF EXISTS sync_identity_offers;
                DROP TABLE IF EXISTS sync_metadata_changes;
                DROP TABLE IF EXISTS sync_suppression;
                DROP TABLE IF EXISTS sync_source_bindings;
                DROP TABLE IF EXISTS sync_video_content_identities;
                DROP TABLE IF EXISTS sync_exports;
                DROP TABLE IF EXISTS sync_outbox;
                DROP TABLE IF EXISTS sync_operation_receipts;
                DROP TABLE IF EXISTS sync_entity_identities;
                DROP TABLE IF EXISTS sync_publishers;
                DROP TABLE IF EXISTS sync_purge_journal;
                DROP TABLE IF EXISTS sync_space_state;
                DROP TABLE IF EXISTS library_identity;
                """;
            reset.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info('metadata_edits') WHERE name='video_content_identity'";
        if (command.ExecuteScalar() is null) return;
        command.CommandText = "DROP TRIGGER IF EXISTS video_metadata_bind_verified_content; DROP TABLE IF EXISTS video_metadata_operation_bindings; ALTER TABLE metadata_edits DROP COLUMN video_content_identity; PRAGMA user_version=44;";
        command.ExecuteNonQuery();
    }

    internal static void RevertVersion44(SqliteConnection connection)
    {
        RevertVersion45(connection);
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='video_contents'";
        if (probe.ExecuteScalar() is null) return;
        Execute("PRAGMA foreign_keys=OFF");
        try
        {
            using var transaction = connection.BeginTransaction();
            probe.Transaction = transaction;
            var triggers = new List<string>();
            probe.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' AND (name LIKE 'mirror_%' OR name LIKE 'video_content_%' OR name LIKE 'description_text_video%')";
            using (var reader = probe.ExecuteReader()) while (reader.Read()) triggers.Add(reader.GetString(0));
            foreach (var name in triggers) Execute("DROP TRIGGER \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
            Execute("DROP TABLE IF EXISTS mirror_media_changes; DROP TABLE IF EXISTS mirror_revision; DROP TABLE IF EXISTS mirror_checkpoint; DROP TABLE IF EXISTS mirror_owned_media;");
            var indexes = new List<string>();
            foreach (var table in VideoContentMigration.Tables)
            {
                probe.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name=$table AND sql IS NOT NULL";
                probe.Parameters.Clear(); probe.Parameters.AddWithValue("$table", table);
                using (var reader = probe.ExecuteReader()) while (reader.Read()) indexes.Add(reader.GetString(0));
                probe.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$table";
                var sql = (string)probe.ExecuteScalar()!;
                sql = Regex.Replace(sql, "^CREATE TABLE\\s+(?:\"" + table + "\"|" + table + ")", "CREATE TABLE " + table + "_legacy", RegexOptions.IgnoreCase);
                sql = Regex.Replace(sql, "REFERENCES\\s+video_contents\\s*\\(id\\)", "REFERENCES videos(id)", RegexOptions.IgnoreCase);
                Execute(sql);
                var columns = new List<string>();
                probe.CommandText = "PRAGMA table_info(" + table + ")"; probe.Parameters.Clear();
                using (var reader = probe.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
                var values = columns.Select(column => column == "video_id"
                    ? "(SELECT MIN(id) FROM videos WHERE content_id=r.video_id)" : "r." + column);
                Execute($"INSERT INTO {table}_legacy({string.Join(',', columns)}) SELECT {string.Join(',', values)} FROM {table} r");
            }
            foreach (var table in VideoContentMigration.Tables.Reverse()) Execute("DROP TABLE " + table);
            foreach (var table in VideoContentMigration.Tables) Execute($"ALTER TABLE {table}_legacy RENAME TO {table}");
            foreach (var sql in indexes) Execute(sql);
            Execute("DROP TABLE video_content_decision_history; DROP INDEX idx_videos_content; ALTER TABLE videos DROP COLUMN content_id; DROP TABLE video_contents; PRAGMA user_version=43;");
            transaction.Commit();
        }
        finally { Execute("PRAGMA foreign_keys=ON"); }

        void Execute(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    }
}
