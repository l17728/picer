using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

/// <summary>
/// Content-owned durable derivatives with record-ID projections for existing UI and sync clients.
/// Deleting a physical record never deletes the content-owned evidence. New copies restore it.
/// All identifiers below come from this fixed schema list, never user input.
/// </summary>
internal static class ContentDerivativeMigration
{
    private sealed record Projection(string Table, string[] Key, bool Face = false);
    private static readonly Projection[] Projections =
    [
        new("detected_faces", ["model_version", "ordinal"]),
        new("vision_analyses", ["analyzer", "model_version"]),
        new("image_visual_concepts", ["concept_id", "model_version"]),
        new("cloud_vision_analyses", ["provider", "model_version", "prompt_version"]),
        new("florence_image_results", ["task", "model_version"]),
        new("face_person_assignments", [], true),
        new("face_person_rejections", ["person_id"], true),
        new("face_review_states", [], true),
        new("face_corrections", [], true)
    ];

    public static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS image_contents(
                sha256 TEXT PRIMARY KEY, file_size INTEGER NOT NULL, ahash INTEGER, dhash INTEGER,
                content_hash_state TEXT NOT NULL DEFAULT 'complete', derivative_version INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE content_derivatives(
                content_sha256 TEXT NOT NULL REFERENCES image_contents(sha256),
                kind TEXT NOT NULL, item_key TEXT NOT NULL, payload TEXT NOT NULL,
                recorded_utc TEXT NOT NULL, PRIMARY KEY(content_sha256,kind,item_key));
            INSERT OR IGNORE INTO image_contents(sha256,file_size,content_hash_state,derivative_version)
                SELECT COALESCE(content_sha256,sha256),file_size,'complete',2 FROM images
                WHERE COALESCE(content_sha256,sha256) IS NOT NULL;
            CREATE TABLE content_text_documents(
                id INTEGER PRIMARY KEY, content_sha256 TEXT NOT NULL UNIQUE REFERENCES image_contents(sha256),
                content TEXT NOT NULL, source_version TEXT NOT NULL, tokenizer_version TEXT NOT NULL);
            CREATE VIRTUAL TABLE content_text_fts USING fts5(content);
            CREATE TRIGGER content_text_ai AFTER INSERT ON content_text_documents BEGIN
                INSERT INTO content_text_fts(rowid,content) VALUES(new.id,search_tokens(new.content));
            END;
            CREATE TRIGGER content_text_au AFTER UPDATE ON content_text_documents BEGIN
                DELETE FROM content_text_fts WHERE rowid=old.id;
                INSERT INTO content_text_fts(rowid,content) VALUES(new.id,search_tokens(new.content));
            END;
            CREATE TRIGGER content_text_ad AFTER DELETE ON content_text_documents BEGIN
                DELETE FROM content_text_fts WHERE rowid=old.id;
            END;
            """);
        var restore = new System.Text.StringBuilder();
        foreach (var projection in Projections)
        {
            var columns = Columns(connection, projection.Table);
            if (columns.Count == 0) continue;
            var values = columns.Where(column => column.Name is not ("id" or "image_id" or "face_id")).ToArray();
            var join = projection.Face ? " JOIN detected_faces f ON f.id=r.face_id JOIN images i ON i.id=f.image_id" : " JOIN images i ON i.id=r.image_id";
            var key = "json_array(" + string.Join(',', (projection.Face ? new[] { "f.model_version", "f.ordinal" } : [])
                .Concat(projection.Key.Select(name => "r." + name))) + ")";
            var json = "json_object(" + string.Join(',', values.Select(column => "'" + column.Name + "'," +
                (column.Type == "BLOB" ? $"CASE WHEN r.{column.Name} IS NULL THEN NULL ELSE hex(r.{column.Name}) END" : "r." + column.Name))) + ")";
            var capture = $"INSERT INTO content_derivatives(content_sha256,kind,item_key,payload,recorded_utc) SELECT COALESCE(i.content_sha256,i.sha256),'{projection.Table}',{key},{json},strftime('%Y-%m-%dT%H:%M:%fZ','now') FROM {projection.Table} r{join} WHERE COALESCE(i.content_sha256,i.sha256) IS NOT NULL";
            var upsert = " ON CONFLICT(content_sha256,kind,item_key) DO UPDATE SET payload=excluded.payload,recorded_utc=excluded.recorded_utc";
            var chronology = columns.FirstOrDefault(column => column.Name == "updated_utc")?.Name
                ?? columns.FirstOrDefault(column => column.Name == "created_utc")?.Name;
            Execute(connection, capture + " ORDER BY " + (chronology is null ? "i.id DESC" : "r." + chronology + ",i.id DESC") + upsert + ";");
            var primary = columns.Where(column => column.PrimaryKey).Select(column => $"r.{column.Name}=new.{column.Name}");
            foreach (var operation in new[] { "INSERT", "UPDATE" })
                Execute(connection, $"CREATE TRIGGER content_capture_{projection.Table}_{operation.ToLowerInvariant()} AFTER {operation} ON {projection.Table} BEGIN {capture} AND {string.Join(" AND ", primary)}{upsert}; END;");
            var deletedKey = key.Replace("r.", "old.", StringComparison.Ordinal);
            var deletedSource = projection.Face ? "detected_faces f JOIN images i ON i.id=f.image_id WHERE f.id=old.face_id" : "images i WHERE i.id=old.image_id";
            Execute(connection, $"CREATE TRIGGER content_remove_{projection.Table} AFTER DELETE ON {projection.Table} BEGIN DELETE FROM content_derivatives WHERE kind='{projection.Table}' AND (content_sha256,item_key) IN (SELECT COALESCE(i.content_sha256,i.sha256),{deletedKey} FROM {deletedSource}); END;");

            var targetColumns = new[] { projection.Face ? "face_id" : "image_id" }.Concat(values.Select(column => column.Name));
            var faceId = "(SELECT id FROM detected_faces WHERE image_id=new.id AND model_version=json_extract(d.item_key,'$[0]') AND ordinal=json_extract(d.item_key,'$[1]'))";
            var expressions = new[] { projection.Face ? faceId : "new.id" }.Concat(values.Select(column => column.Name switch
            {
                "source_file_size" => "new.file_size",
                "source_modified_utc" => "new.modified_utc",
                "old_person_id" or "new_person_id" => $"(SELECT id FROM people WHERE id=json_extract(d.payload,'$.{column.Name}'))",
                _ when column.Type == "BLOB" => $"unhex_blob(json_extract(d.payload,'$.{column.Name}'))",
                _ => $"json_extract(d.payload,'$.{column.Name}')"
            }));
            var eligibility = projection.Face ? $" AND {faceId} IS NOT NULL" : "";
            if (values.Any(column => column.Name == "person_id"))
                eligibility += " AND EXISTS(SELECT 1 FROM people WHERE id=json_extract(d.payload,'$.person_id'))";
            if (projection.Table == "face_corrections")
                eligibility += $" AND NOT EXISTS(SELECT 1 FROM face_corrections existing WHERE existing.face_id={faceId} AND existing.action=json_extract(d.payload,'$.action') AND existing.created_utc=json_extract(d.payload,'$.created_utc'))";
            restore.Append($"INSERT OR IGNORE INTO {projection.Table}({string.Join(',', targetColumns)}) SELECT {string.Join(',', expressions)} FROM content_derivatives d WHERE d.content_sha256=COALESCE(new.content_sha256,new.sha256) AND d.kind='{projection.Table}'{eligibility} ORDER BY d.recorded_utc DESC;");
        }
        var restoreBody = $"""
                INSERT OR IGNORE INTO image_contents(sha256,file_size,content_hash_state,derivative_version)
                    VALUES(COALESCE(new.content_sha256,new.sha256),new.file_size,'complete',2);
                {restore}
                INSERT OR IGNORE INTO media_text_documents(media_id,media_type,content,fingerprint,source_version,updated_utc)
                    SELECT new.id,'photo',new.path || ' ' || content,'content:' || content_sha256,source_version,strftime('%Y-%m-%dT%H:%M:%fZ','now')
                    FROM content_text_documents WHERE content_sha256=COALESCE(new.content_sha256,new.sha256);
            """;
        Execute(connection, $"CREATE TRIGGER content_restore_image AFTER INSERT ON images WHEN COALESCE(new.content_sha256,new.sha256) IS NOT NULL BEGIN {restoreBody} END;");
        Execute(connection, $"CREATE TRIGGER content_restore_image_update AFTER UPDATE OF content_sha256 ON images WHEN COALESCE(new.content_sha256,new.sha256) IS NOT NULL BEGIN {restoreBody} END;");
        Execute(connection, """
            CREATE TRIGGER content_register_hash_update AFTER UPDATE OF sha256 ON images WHEN new.sha256 IS NOT NULL BEGIN
                INSERT OR IGNORE INTO image_contents(sha256,file_size,content_hash_state,derivative_version)
                    VALUES(new.sha256,new.file_size,'complete',2);
            END;
            """);
        var textCapture = """
            INSERT INTO content_text_documents(content_sha256,content,source_version,tokenizer_version)
            SELECT COALESCE(i.content_sha256,i.sha256),replace(r.content,i.path,''),r.source_version,'han-bigram-v1'
            FROM media_text_documents r JOIN images i ON i.id=r.media_id
            WHERE r.media_type='photo' AND COALESCE(i.content_sha256,i.sha256) IS NOT NULL
            """;
        const string textUpdate = " ON CONFLICT(content_sha256) DO UPDATE SET content=excluded.content,source_version=excluded.source_version,tokenizer_version=excluded.tokenizer_version";
        Execute(connection, textCapture + " ORDER BY r.updated_utc" + textUpdate + ";");
        foreach (var operation in new[] { "INSERT", "UPDATE" })
            Execute(connection, $"CREATE TRIGGER content_capture_text_{operation.ToLowerInvariant()} AFTER {operation} ON media_text_documents BEGIN {textCapture} AND r.media_id=new.media_id{textUpdate}; END;");
        Execute(connection, """
            CREATE TRIGGER content_remove_text AFTER DELETE ON media_text_documents BEGIN
                DELETE FROM content_text_documents WHERE content_sha256 IN (
                    SELECT COALESCE(i.content_sha256,i.sha256) FROM images i WHERE i.id=old.media_id)
                AND NOT EXISTS(SELECT 1 FROM media_text_documents m JOIN images other ON other.id=m.media_id
                    WHERE COALESCE(other.content_sha256,other.sha256)=content_text_documents.content_sha256);
            END;
            """);
        Execute(connection, "UPDATE images SET content_sha256=COALESCE(content_sha256,sha256) WHERE COALESCE(content_sha256,sha256) IS NOT NULL;");
        DescriptionTextProjection.Install(connection);
        Execute(connection, "PRAGMA user_version=41;");
        transaction.Commit();
    }

    private sealed record Column(string Name, string Type, bool PrimaryKey);
    private static List<Column> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        var result = new List<Column>();
        while (reader.Read()) result.Add(new(reader.GetString(1), reader.GetString(2).ToUpperInvariant(), reader.GetInt32(5) > 0));
        return result;
    }
    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}
