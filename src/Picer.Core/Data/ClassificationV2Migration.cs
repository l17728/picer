using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class ClassificationV2Migration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS classification_v2_taxonomy(
                id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL, discovery INTEGER NOT NULL,
                payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS classification_v2_settings(id INTEGER PRIMARY KEY CHECK(id=1),payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS classification_v2_plans(
                id TEXT PRIMARY KEY,request_id TEXT NOT NULL UNIQUE,created_utc TEXT NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS classification_v2_jobs(
                id TEXT PRIMARY KEY,request_id TEXT NOT NULL UNIQUE,plan_id TEXT NOT NULL UNIQUE,
                state TEXT NOT NULL,config TEXT NOT NULL,budget INTEGER NOT NULL,used INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL,actor TEXT);
            CREATE TABLE IF NOT EXISTS classification_v2_work(
                image_id INTEGER PRIMARY KEY REFERENCES images(id) ON DELETE CASCADE,
                success_input TEXT,success_fingerprint TEXT,attempt_fingerprint TEXT,
                outcome TEXT NOT NULL DEFAULT 'unprocessed',error_code TEXT,retry_after TEXT);
            CREATE TABLE IF NOT EXISTS classification_v2_items(
                job_id TEXT NOT NULL REFERENCES classification_v2_jobs(id) ON DELETE CASCADE,
                image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                fingerprint TEXT NOT NULL,input TEXT NOT NULL,state TEXT NOT NULL DEFAULT 'pending',
                lease TEXT,lease_until TEXT,attempts INTEGER NOT NULL DEFAULT 0,error_code TEXT,
                PRIMARY KEY(job_id,image_id));
            CREATE INDEX IF NOT EXISTS classification_v2_pending ON classification_v2_items(state,lease_until,job_id,image_id);
            CREATE UNIQUE INDEX IF NOT EXISTS classification_v2_one_active ON classification_v2_items(image_id)
                WHERE state IN ('pending','leased');
            CREATE TABLE IF NOT EXISTS classification_v2_results(
                image_id INTEGER PRIMARY KEY REFERENCES images(id) ON DELETE CASCADE,
                fingerprint TEXT NOT NULL,input TEXT NOT NULL,payload TEXT NOT NULL,updated_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS classification_v2_labels(
                image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                label_id TEXT NOT NULL,PRIMARY KEY(image_id,label_id));
            CREATE INDEX IF NOT EXISTS classification_v2_label_images ON classification_v2_labels(label_id,image_id);
            CREATE TABLE IF NOT EXISTS classification_v2_proposals(
                id TEXT PRIMARY KEY,owner_key TEXT NOT NULL,revision INTEGER NOT NULL DEFAULT 1,
                state TEXT NOT NULL DEFAULT 'proposed',payload TEXT NOT NULL,created_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS classification_v2_changes(
                operation_id TEXT PRIMARY KEY,payload_hash TEXT NOT NULL,result TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS classification_v2_cluster_tasks(
                id TEXT PRIMARY KEY,request_id TEXT NOT NULL UNIQUE,state TEXT NOT NULL,payload TEXT NOT NULL,
                lease TEXT,lease_until TEXT,error_code TEXT,created_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS classification_v2_mirror(
                origin TEXT PRIMARY KEY,epoch TEXT NOT NULL,revision TEXT NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS classification_v2_outbox(
                operation_id TEXT PRIMARY KEY,origin TEXT NOT NULL,route TEXT NOT NULL,payload TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'pending',error_code TEXT);
            CREATE TRIGGER IF NOT EXISTS classification_v2_image_removed AFTER DELETE ON images BEGIN
                DELETE FROM classification_v2_plans WHERE EXISTS(SELECT 1 FROM json_each(payload,'$.Items') WHERE json_extract(value,'$.ImageId')=OLD.id);
                DELETE FROM classification_v2_cluster_tasks WHERE EXISTS(SELECT 1 FROM json_each(payload,'$.Representatives') WHERE json_extract(value,'$.Image.Id')=OLD.id);
                UPDATE classification_v2_proposals SET revision=revision+1,payload=json_set(payload,'$.Revision',revision+1,'$.ImageIds',json(
                    (SELECT json_group_array(value) FROM json_each(payload,'$.ImageIds') WHERE value<>OLD.id)))
                    WHERE EXISTS(SELECT 1 FROM json_each(payload,'$.ImageIds') WHERE value=OLD.id);
                DELETE FROM classification_v2_proposals WHERE json_array_length(payload,'$.ImageIds')=0;
                DELETE FROM classification_v2_mirror;
            END;
            PRAGMA user_version=93;
            """;
        command.ExecuteNonQuery();
        command.CommandText = "CREATE TABLE IF NOT EXISTS classification_v2_revision(id INTEGER PRIMARY KEY CHECK(id=1),input_revision INTEGER NOT NULL); INSERT OR IGNORE INTO classification_v2_revision VALUES(1,1);";
        command.ExecuteNonQuery();
        foreach (var table in new[] { "images", "cloud_vision_analyses", "face_person_assignments", "detected_faces", "sync_source_bindings", "library_identity", "classification_v2_taxonomy", "classification_v2_settings" })
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                command.CommandText = $"CREATE TRIGGER IF NOT EXISTS classification_v2_revision_{table}_{operation} AFTER {operation} ON {table} BEGIN UPDATE classification_v2_revision SET input_revision=input_revision+1 WHERE id=1; END;";
                command.ExecuteNonQuery();
            }
        transaction.Commit();
    }
}
