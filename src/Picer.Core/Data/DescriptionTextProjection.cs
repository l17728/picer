using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class DescriptionTextProjection
{
    public static void Install(SqliteConnection connection)
    {
        const string photos = """
            INSERT INTO media_text_documents(media_id,media_type,content,fingerprint,source_version,updated_utc)
            SELECT i.id,'photo',i.path || ' ' || COALESCE(i.camera,'') || ' ' || COALESCE(i.gps,'') || ' ' ||
                COALESCE((SELECT group_concat(COALESCE(c.summary,'') || ' ' || COALESCE(c.result_json,''),' ')
                    FROM cloud_vision_analyses c WHERE c.image_id=i.id AND c.status='completed'),''),
                'description-sync','description-sync-v1',strftime('%Y-%m-%dT%H:%M:%fZ','now') FROM images i
            WHERE i.id=$id
            ON CONFLICT(media_id) DO UPDATE SET content=excluded.content,fingerprint=excluded.fingerprint,
                source_version=excluded.source_version,updated_utc=excluded.updated_utc;
            """;
        const string videos = """
            INSERT INTO media_text_documents(media_id,media_type,content,fingerprint,source_version,updated_utc)
            SELECT -v.id,'video',v.path || ' ' || COALESCE(v.gps,'') || ' ' ||
                COALESCE((SELECT group_concat(COALESCE(s.summary,'') || ' ' || COALESCE(s.result_json,''),' ')
                    FROM video_summaries s WHERE s.video_id=v.id),'') || ' ' ||
                COALESCE((SELECT group_concat(t.transcript_json,' ') FROM video_transcripts t WHERE t.video_id=v.id),''),
                'description-sync','description-sync-v1',strftime('%Y-%m-%dT%H:%M:%fZ','now') FROM videos v
            WHERE v.id=$id
            ON CONFLICT(media_id) DO UPDATE SET content=excluded.content,fingerprint=excluded.fingerprint,
                source_version=excluded.source_version,updated_utc=excluded.updated_utc;
            """;
        var hasVideoTables = Exists(connection, "video_summaries") && Exists(connection, "video_transcripts");
        foreach (var (table, key, sql) in new[] { ("cloud_vision_analyses", "image_id", photos), ("video_summaries", "video_id", videos), ("video_transcripts", "video_id", videos) })
        {
            if (!Exists(connection, table) || key == "video_id" && !hasVideoTables) continue;
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var row = operation == "DELETE" ? "old" : "new";
                Execute(connection, $"CREATE TRIGGER description_text_{table}_{operation.ToLowerInvariant()} AFTER {operation} ON {table} BEGIN {sql.Replace("$id", row + "." + key, StringComparison.Ordinal)} END;");
            }
        }
        if (Exists(connection, "cloud_vision_analyses")) Execute(connection, photos.Replace("i.id=$id", "EXISTS(SELECT 1 FROM cloud_vision_analyses c WHERE c.image_id=i.id AND c.status='completed')", StringComparison.Ordinal));
        if (hasVideoTables) Execute(connection, videos.Replace("v.id=$id", "EXISTS(SELECT 1 FROM video_summaries s WHERE s.video_id=v.id) OR EXISTS(SELECT 1 FROM video_transcripts t WHERE t.video_id=v.id)", StringComparison.Ordinal));
    }

    private static bool Exists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table";
        command.Parameters.AddWithValue("$table", table);
        return command.ExecuteScalar() is not null;
    }

    private static void Execute(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
}
