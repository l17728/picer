using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace Picer.Core.Data;

internal static class VideoFaceOwnershipMigration
{
    // Face identities include the owner of the human annotation. The video
    // content and keyframe indexes continue to be shared by all references.
    internal static void Apply(SqliteConnection connection)
    {
        void Execute(string sql) { using var command=connection.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery(); }
        Execute("PRAGMA foreign_keys=OFF; PRAGMA legacy_alter_table=ON;");
        try
        {
            using var transaction=connection.BeginTransaction();
            var objects=new List<(string Type,string Name,string Sql)>();
            using(var query=connection.CreateCommand())
            {
                query.CommandText="SELECT type,name,sql FROM sqlite_master WHERE tbl_name='video_detected_faces' AND type IN ('index','trigger') AND sql IS NOT NULL";
                using var rows=query.ExecuteReader();while(rows.Read()) objects.Add((rows.GetString(0),rows.GetString(1),rows.GetString(2)));
            }
            var columns=new List<string>();string schema;
            using(var query=connection.CreateCommand())
            {
                query.CommandText="PRAGMA table_info(video_detected_faces)";
                using(var rows=query.ExecuteReader()) while(rows.Read()) columns.Add(rows.GetString(1));
                query.CommandText="SELECT sql FROM sqlite_master WHERE type='table' AND name='video_detected_faces'";
                schema=(string)query.ExecuteScalar()!;
            }
            if(!columns.Contains("owner_key"))
            {
            foreach(var item in objects) Execute($"DROP {item.Type} \"{item.Name.Replace("\"","\"\"")}\"");
            Execute("ALTER TABLE video_detected_faces RENAME TO video_faces_before_ownership");
            schema=Regex.Replace(schema,@"UNIQUE\s*\(\s*keyframe_id\s*,\s*model_version\s*,\s*ordinal\s*\)",
                "owner_key TEXT NOT NULL DEFAULT '', UNIQUE(keyframe_id,model_version,ordinal,owner_key)",RegexOptions.IgnoreCase);
            if(!schema.Contains("owner_key",StringComparison.Ordinal))
            {
                var closing=schema.LastIndexOf(')');
                if(closing<0) throw new InvalidDataException("video_face_ownership_schema_unrecognized");
                schema=schema.Insert(closing,", owner_key TEXT NOT NULL DEFAULT '', UNIQUE(keyframe_id,model_version,ordinal,owner_key)");
            }
            Execute(schema);
            Execute($"""
                INSERT INTO video_detected_faces({string.Join(',',columns)},owner_key)
                SELECT {string.Join(',',columns.Select(column=>"f."+column))},
                    CASE WHEN COUNT(*) OVER(PARTITION BY keyframe_id,model_version,ordinal)>1
                        THEN 'ambiguous-content:'||f.video_id||':face:'||f.id
                    ELSE COALESCE((SELECT MIN(owner_key) FROM sync_video_owners o WHERE o.content_id=f.video_id
                        HAVING COUNT(DISTINCT owner_key)=1 AND MIN(owner_key) NOT LIKE 'ambiguous-source:%'),
                        'ambiguous-content:'||f.video_id) END
                FROM video_faces_before_ownership f;
                DROP TABLE video_faces_before_ownership;
                """);
            foreach(var item in objects) Execute(item.Sql);
            }
            Execute("""
                UPDATE video_detected_faces SET owner_key=COALESCE(
                    (SELECT MIN(owner_key) FROM sync_video_owners o WHERE o.content_id=video_detected_faces.video_id
                        HAVING COUNT(DISTINCT owner_key)=1 AND MIN(owner_key) NOT LIKE 'ambiguous-source:%'),
                    'ambiguous-content:'||video_id) WHERE owner_key='';
                CREATE INDEX IF NOT EXISTS ix_video_face_owner ON video_detected_faces(video_id,owner_key,model_version);
                CREATE VIEW IF NOT EXISTS video_effective_faces AS
                    SELECT o.video_id AS reference_id,f.* FROM sync_video_owners o
                    JOIN video_detected_faces f ON f.video_id=o.content_id AND f.owner_key=o.owner_key
                    WHERE o.owner_key NOT LIKE 'ambiguous-source:%';
                CREATE TABLE IF NOT EXISTS video_face_ownership_reviews(
                    face_id INTEGER PRIMARY KEY REFERENCES video_detected_faces(id) ON DELETE CASCADE,
                    operation_id TEXT NOT NULL UNIQUE,
                    state TEXT NOT NULL DEFAULT 'pending',resolution_json TEXT);
                INSERT OR IGNORE INTO video_face_ownership_reviews(face_id,operation_id)
                    SELECT id,lower(hex(randomblob(16))) FROM video_detected_faces WHERE owner_key LIKE 'ambiguous-content:%';
                CREATE TRIGGER IF NOT EXISTS video_face_owner_default AFTER INSERT ON video_detected_faces WHEN NEW.owner_key='' BEGIN
                    UPDATE video_detected_faces SET owner_key=COALESCE(
                        (SELECT MIN(owner_key) FROM sync_video_owners WHERE content_id=NEW.video_id
                            HAVING COUNT(DISTINCT owner_key)=1 AND MIN(owner_key) NOT LIKE 'ambiguous-source:%'),
                        'ambiguous-content:'||NEW.video_id) WHERE id=NEW.id;
                    INSERT OR IGNORE INTO video_face_ownership_reviews(face_id,operation_id)
                        SELECT id,lower(hex(randomblob(16))) FROM video_detected_faces WHERE id=NEW.id AND owner_key LIKE 'ambiguous-content:%';
                END;
                """);
            // The v43 content merge predates owner-scoped face identities. Keep
            // its other evidence-transfer rules, but never match another owner's
            // face when deciding whether to retain a row or copy a rejection.
            using(var query=connection.CreateCommand())
            {
                query.CommandText="SELECT sql FROM sqlite_master WHERE type='trigger' AND name='video_content_rebind'";
                if(query.ExecuteScalar() is string rebind)
                {
                    // Keep legacy tag/concept conversion in the same trigger,
                    // before garbage collection. SQLite does not guarantee an
                    // execution order between separate AFTER triggers.
                    query.CommandText="SELECT sql FROM sqlite_master WHERE type='trigger' AND name='space_sync_video_verify_legacy'";
                    if(query.ExecuteScalar() is string legacy)
                    {
                        if(!rebind.Contains("/* legacy_owner_transfer_start */",StringComparison.Ordinal))
                        {
                            var start=legacy.IndexOf("BEGIN",StringComparison.OrdinalIgnoreCase)+5;
                            var end=legacy.LastIndexOf("END",StringComparison.OrdinalIgnoreCase);
                            if(start<5 || end<=start) throw new InvalidDataException("video_legacy_transfer_schema_unrecognized");
                            rebind=rebind.Replace("INSERT INTO video_content_decision_history",
                                "/* legacy_owner_transfer_start */\n"+legacy[start..end]+"\n/* legacy_owner_transfer_end */\nINSERT INTO video_content_decision_history",StringComparison.Ordinal);
                        }
                        Execute("DROP TRIGGER space_sync_video_verify_legacy");
                    }
                    if(!rebind.Contains("target.owner_key",StringComparison.Ordinal))
                    rebind=rebind.Replace("target.ordinal=video_detected_faces.ordinal",
                        "target.ordinal=video_detected_faces.ordinal AND target.owner_key=video_detected_faces.owner_key",StringComparison.Ordinal)
                        .Replace("target.model_version=source.model_version",
                            "target.model_version=source.model_version AND target.owner_key=source.owner_key",StringComparison.Ordinal);
                    // A collision within one owner can contain two contradictory
                    // manual decisions. Retain the incoming face and rejections
                    // for explicit review instead of dropping it on content GC.
                    if(!rebind.Contains("video_face_ownership_reviews",StringComparison.Ordinal))
                        rebind=rebind.Replace("UPDATE video_detected_faces SET video_id=new.content_id,","""
                            UPDATE video_detected_faces SET owner_key='ambiguous-content:'||new.content_id||':face:'||id
                            WHERE video_id=old.content_id AND EXISTS(SELECT 1 FROM video_detected_faces target
                                WHERE target.video_id=new.content_id AND target.timestamp_seconds=video_detected_faces.timestamp_seconds
                                    AND target.model_version=video_detected_faces.model_version AND target.ordinal=video_detected_faces.ordinal
                                    AND target.owner_key=video_detected_faces.owner_key);
                            INSERT OR IGNORE INTO video_face_ownership_reviews(face_id,operation_id)
                                SELECT id,lower(hex(randomblob(16))) FROM video_detected_faces
                                WHERE video_id=old.content_id AND owner_key LIKE 'ambiguous-content:%';
                            UPDATE video_detected_faces SET video_id=new.content_id,
                            """,StringComparison.Ordinal);
                    Execute("DROP TRIGGER video_content_rebind");
                    Execute(rebind);
                }
            }
            using(var check=connection.CreateCommand())
            {
                check.CommandText="PRAGMA foreign_key_check";using var rows=check.ExecuteReader();
                if(rows.Read()) throw new InvalidDataException("video_face_ownership_foreign_key_failure");
            }
            Execute("PRAGMA user_version=71");transaction.Commit();
        }
        finally { Execute("PRAGMA legacy_alter_table=OFF; PRAGMA foreign_keys=ON;"); }
    }
}
