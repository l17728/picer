using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace Picer.Core.Data;

internal static class VideoContentMigration
{
    internal static readonly string[] Tables = ["video_keyframes", "video_transcripts", "video_summaries", "video_detected_faces", "video_tags", "video_visual_concepts", "video_face_person_rejections"];

    public static void Apply(SqliteConnection connection, Action<SqliteConnection>? afterMigration = null)
    {
        Execute(connection, "PRAGMA foreign_keys=OFF");
        try
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, """
                CREATE TABLE video_contents(id INTEGER PRIMARY KEY AUTOINCREMENT,sha256 TEXT UNIQUE,file_size INTEGER NOT NULL);
                ALTER TABLE videos ADD COLUMN content_id INTEGER REFERENCES video_contents(id);
                INSERT INTO video_contents(id,sha256,file_size)
                    SELECT MIN(id),lower(sha256),MAX(file_size) FROM videos WHERE length(sha256)=64 GROUP BY lower(sha256);
                INSERT INTO video_contents(id,sha256,file_size)
                    SELECT id,NULL,file_size FROM videos WHERE sha256 IS NULL OR length(sha256)<>64;
                UPDATE videos SET sha256=lower(sha256),content_id=CASE WHEN length(sha256)=64 THEN
                    (SELECT id FROM video_contents c WHERE c.sha256=lower(videos.sha256)) ELSE id END;
                CREATE INDEX idx_videos_content ON videos(content_id);
                CREATE TABLE video_content_decision_history(
                    content_id INTEGER NOT NULL REFERENCES video_contents(id) ON DELETE CASCADE,
                    original_face_id INTEGER NOT NULL,person_id INTEGER,state TEXT,updated_utc TEXT,
                    PRIMARY KEY(content_id,original_face_id));
                CREATE TEMP TABLE vc_frame_map AS SELECT k.id old_id,
                    MIN(k.id) OVER(PARTITION BY v.content_id,k.timestamp_seconds) new_id
                    FROM video_keyframes k JOIN videos v ON v.id=k.video_id;
                CREATE TEMP TABLE vc_face_map AS SELECT f.id old_id,
                    FIRST_VALUE(f.id) OVER(PARTITION BY v.content_id,m.new_id,f.model_version,f.ordinal
                        ORDER BY CASE WHEN f.assignment_state='confirmed' THEN 0 WHEN f.assignment_state='ignored' THEN 1 ELSE 2 END,
                        f.updated_utc DESC,f.id) new_id
                    FROM video_detected_faces f JOIN videos v ON v.id=f.video_id JOIN vc_frame_map m ON m.old_id=f.keyframe_id;
                INSERT INTO video_content_decision_history
                    SELECT v.content_id,f.id,f.assigned_person_id,f.assignment_state,f.updated_utc
                    FROM video_detected_faces f JOIN videos v ON v.id=f.video_id JOIN vc_face_map m ON m.old_id=f.id
                    WHERE m.old_id<>m.new_id AND f.assignment_state IS NOT NULL;
                """);
            var restoreSql = new List<string>();
            foreach (var table in Tables)
            {
                using (var objects = connection.CreateCommand())
                {
                    objects.CommandText = "SELECT sql FROM sqlite_master WHERE tbl_name=$table AND type IN ('index','trigger') AND sql IS NOT NULL";
                    objects.Parameters.AddWithValue("$table", table);
                    using var reader = objects.ExecuteReader(); while (reader.Read()) restoreSql.Add(reader.GetString(0));
                }
                var columns = Columns(connection, table);
                using var schema = connection.CreateCommand(); schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$table";
                schema.Parameters.AddWithValue("$table", table);
                var sql = (string)schema.ExecuteScalar()!;
                sql = Regex.Replace(sql, "^CREATE TABLE\\s+(?:\"" + table + "\"|" + table + ")", "CREATE TABLE " + table + "_content_new", RegexOptions.IgnoreCase);
                sql = Regex.Replace(sql, "REFERENCES\\s+videos\\s*\\(id\\)", "REFERENCES video_contents(id)", RegexOptions.IgnoreCase);
                Execute(connection, sql);
                var selection = columns.Select(column => column switch
                {
                    "video_id" => "v.content_id",
                    "keyframe_id" when table == "video_detected_faces" => "(SELECT new_id FROM vc_frame_map WHERE old_id=r.keyframe_id)",
                    "video_face_id" => "(SELECT new_id FROM vc_face_map WHERE old_id=r.video_face_id)",
                    _ => "r." + column
                });
                var from = table == "video_face_person_rejections" ? "" : " JOIN videos v ON v.id=r.video_id";
                var where = table == "video_keyframes" ? " WHERE r.id=(SELECT new_id FROM vc_frame_map WHERE old_id=r.id)"
                    : table == "video_detected_faces" ? " WHERE r.id=(SELECT new_id FROM vc_face_map WHERE old_id=r.id)" : "";
                var ordering = columns.Contains("created_utc") && !columns.Contains("id") ? " ORDER BY r.created_utc" : "";
                Execute(connection, $"INSERT OR REPLACE INTO {table}_content_new({string.Join(',', columns)}) SELECT {string.Join(',', selection)} FROM {table} r{from}{where}{ordering}");
            }
            foreach (var table in Tables.Reverse()) Execute(connection, $"DROP TABLE {table}");
            foreach (var table in Tables) Execute(connection, $"ALTER TABLE {table}_content_new RENAME TO {table}");
            foreach (var sql in restoreSql.Where(sql => !sql.Contains("description_text_video_", StringComparison.Ordinal))) Execute(connection, sql);
            Execute(connection, "DROP TABLE vc_frame_map; DROP TABLE vc_face_map;");
            InstallReferenceTriggers(connection);
            InstallTextProjection(connection);
            afterMigration?.Invoke(connection);
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA foreign_key_check";
                using var violations = check.ExecuteReader();
                if (violations.Read()) throw new InvalidOperationException("video_content_migration_foreign_key_failure");
            }
            transaction.Commit();
        }
        finally { Execute(connection, "PRAGMA foreign_keys=ON"); }
    }

    private static void InstallReferenceTriggers(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TRIGGER video_content_bind_insert AFTER INSERT ON videos BEGIN
                INSERT INTO video_contents(sha256,file_size) VALUES(CASE WHEN length(new.sha256)=64 THEN lower(new.sha256) ELSE NULL END,new.file_size) ON CONFLICT(sha256) DO NOTHING;
                UPDATE videos SET content_id=CASE WHEN length(new.sha256)=64 THEN
                    (SELECT id FROM video_contents WHERE sha256=lower(new.sha256)) ELSE last_insert_rowid() END WHERE id=new.id;
                UPDATE videos SET
                    duration_seconds=COALESCE(duration_seconds,(SELECT MAX(other.duration_seconds) FROM videos other WHERE other.content_id=videos.content_id)),
                    width=MAX(width,COALESCE((SELECT MAX(other.width) FROM videos other WHERE other.content_id=videos.content_id),0)),
                    height=MAX(height,COALESCE((SELECT MAX(other.height) FROM videos other WHERE other.content_id=videos.content_id),0)),
                    person_analysis_status=COALESCE(person_analysis_status,(SELECT other.person_analysis_status FROM videos other WHERE other.content_id=videos.content_id AND other.person_analysis_status='completed' LIMIT 1)),
                    person_analyzed_utc=COALESCE(person_analyzed_utc,(SELECT MAX(other.person_analyzed_utc) FROM videos other WHERE other.content_id=videos.content_id))
                WHERE id=new.id;

            END;
            CREATE TRIGGER video_content_reference_delete AFTER DELETE ON videos BEGIN
                DELETE FROM video_contents WHERE id=old.content_id AND NOT EXISTS(SELECT 1 FROM videos WHERE content_id=old.content_id);
            END;
            CREATE TRIGGER video_content_text_delete AFTER DELETE ON video_contents BEGIN
                DELETE FROM media_text_documents WHERE media_id=-old.id;
            END;
            """);
        Execute(connection, """
            CREATE TRIGGER video_content_forget_hash AFTER UPDATE OF sha256 ON videos
            WHEN old.sha256 IS NOT NULL AND (new.sha256 IS NULL OR length(new.sha256)<>64)
                AND new.content_id=old.content_id AND (new.file_size<>old.file_size OR new.modified_utc<>old.modified_utc) BEGIN
                INSERT INTO video_contents(sha256,file_size) VALUES(NULL,new.file_size);
                UPDATE videos SET content_id=last_insert_rowid() WHERE id=new.id;
            END;
            CREATE TRIGGER video_content_rebind_cleanup AFTER UPDATE OF content_id ON videos
            WHEN old.content_id IS NOT NULL AND new.content_id<>old.content_id
                AND ((SELECT sha256 FROM video_contents WHERE id=old.content_id) IS NOT NULL
                    OR (SELECT sha256 FROM video_contents WHERE id=new.content_id) IS NULL) BEGIN
                DELETE FROM video_contents WHERE id=old.content_id AND NOT EXISTS(SELECT 1 FROM videos WHERE content_id=old.content_id);
            END;
            CREATE TRIGGER video_content_bind_hash AFTER UPDATE OF sha256 ON videos
            WHEN length(new.sha256)=64 AND lower(new.sha256) IS NOT lower(old.sha256) BEGIN
                UPDATE video_contents SET sha256=lower(new.sha256),file_size=new.file_size
                    WHERE id=new.content_id AND sha256 IS NULL
                    AND NOT EXISTS(SELECT 1 FROM video_contents WHERE sha256=lower(new.sha256));
                INSERT INTO video_contents(sha256,file_size) VALUES(lower(new.sha256),new.file_size) ON CONFLICT(sha256) DO NOTHING;
                UPDATE videos SET content_id=(SELECT id FROM video_contents WHERE sha256=lower(new.sha256)) WHERE id=new.id;
            END;
            """);
        // Unknown-hash legacy evidence is transferred when its reference joins a
        // verified content identity. Existing shared evidence is not replaced.
        var move = new System.Text.StringBuilder();
        foreach (var table in new[] { "video_transcripts", "video_summaries", "video_tags", "video_visual_concepts" })
        {
            var key = table == "video_tags" ? $" AND target.tag_id={table}.tag_id"
                : table == "video_visual_concepts" ? $" AND target.concept_id={table}.concept_id AND target.model_version={table}.model_version" : "";
            move.Append($"UPDATE {table} SET video_id=new.content_id WHERE video_id=old.content_id AND NOT EXISTS(SELECT 1 FROM {table} target WHERE target.video_id=new.content_id{key});");
        }
        Execute(connection, $"""
            CREATE TRIGGER video_content_rebind AFTER UPDATE OF content_id ON videos
            WHEN old.content_id IS NOT NULL AND new.content_id<>old.content_id
                AND (SELECT sha256 FROM video_contents WHERE id=old.content_id) IS NULL
                AND (SELECT sha256 FROM video_contents WHERE id=new.content_id) IS NOT NULL BEGIN
                INSERT INTO video_content_decision_history
                    SELECT new.content_id,id,assigned_person_id,assignment_state,updated_utc
                    FROM video_detected_faces WHERE video_id=old.content_id AND assignment_state IS NOT NULL ON CONFLICT DO NOTHING;
                {move}
                UPDATE video_keyframes SET video_id=new.content_id WHERE video_id=old.content_id
                    AND NOT EXISTS(SELECT 1 FROM video_keyframes other WHERE other.video_id=new.content_id AND other.timestamp_seconds=video_keyframes.timestamp_seconds);
                UPDATE video_detected_faces SET video_id=new.content_id,
                    keyframe_id=COALESCE((SELECT id FROM video_keyframes k WHERE k.video_id=new.content_id AND k.timestamp_seconds=video_detected_faces.timestamp_seconds ORDER BY id LIMIT 1),keyframe_id)
                    WHERE video_id=old.content_id AND NOT EXISTS(SELECT 1 FROM video_detected_faces target
                        WHERE target.video_id=new.content_id AND target.timestamp_seconds=video_detected_faces.timestamp_seconds
                            AND target.model_version=video_detected_faces.model_version AND target.ordinal=video_detected_faces.ordinal);
                INSERT INTO video_face_person_rejections(video_face_id,person_id,created_utc)
                    SELECT target.id,r.person_id,r.created_utc FROM video_face_person_rejections r
                    JOIN video_detected_faces source ON source.id=r.video_face_id AND source.video_id=old.content_id
                    JOIN video_detected_faces target ON target.video_id=new.content_id
                        AND target.timestamp_seconds=source.timestamp_seconds AND target.ordinal=source.ordinal AND target.model_version=source.model_version WHERE 1 ON CONFLICT DO NOTHING;
                DELETE FROM video_contents WHERE id=old.content_id AND NOT EXISTS(SELECT 1 FROM videos WHERE content_id=old.content_id);
            END;
            """);
    }

    internal static void InstallTextProjection(SqliteConnection connection)
    {
        const string body = """
            INSERT INTO media_text_documents(media_id,media_type,content,fingerprint,source_version,updated_utc)
            SELECT -c.id,'video',COALESCE((SELECT group_concat(summary || ' ' || result_json,' ') FROM video_summaries WHERE video_id=c.id),'') || ' ' ||
                COALESCE((SELECT group_concat(transcript_json,' ') FROM video_transcripts WHERE video_id=c.id),''),
                'video-content','video-content-v1',strftime('%Y-%m-%dT%H:%M:%fZ','now') FROM video_contents c WHERE c.id=$id
            ON CONFLICT(media_id) DO UPDATE SET content=excluded.content,fingerprint=excluded.fingerprint,source_version=excluded.source_version,updated_utc=excluded.updated_utc;
            """;
        foreach (var table in new[] { "video_summaries", "video_transcripts" })
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
                Execute(connection, $"CREATE TRIGGER description_text_{table}_{operation.ToLowerInvariant()} AFTER {operation} ON {table} BEGIN {body.Replace("$id", (operation == "DELETE" ? "old" : "new") + ".video_id", StringComparison.Ordinal)} END");
        Execute(connection, "DELETE FROM media_text_documents WHERE media_type='video'");
        Execute(connection, body.Replace("c.id=$id", "EXISTS(SELECT 1 FROM video_summaries WHERE video_id=c.id) OR EXISTS(SELECT 1 FROM video_transcripts WHERE video_id=c.id)", StringComparison.Ordinal));
    }

    private static string[] Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA table_info(" + table + ")";
        using var reader = command.ExecuteReader(); var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(1)); return values.ToArray();
    }

    private static void Execute(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
}
