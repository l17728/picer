using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    /// <summary>Stable operation identities survive server version promotion and local ID remapping.
    /// The digest contains no names, embeddings or original media paths.</summary>
    public string GetCatalogRetirementFingerprint(string kind,long catalogId,string mediaKind,long mediaId,
        SqliteTransaction? transaction=null,string? publisher=null)
    {
        if(kind is not ("tag" or "album" or "person") || mediaKind is not ("image" or "video")
            || kind=="album" && mediaKind=="video" || catalogId<=0 || mediaId<=0)
            throw new ArgumentException("invalid_catalog_retirement_reference");
        // Separate indexed queries avoid scanning every metadata kind once per
        // media item when a large catalogue object is deleted.
        var sql=(kind,mediaKind) switch
        {
            ("tag","image")=>"SELECT operation_id,server_version FROM metadata_edits WHERE kind='tag-link' AND entity_id=$id AND field=CAST($catalog AS TEXT)",
            ("album","image")=>"SELECT operation_id,server_version FROM metadata_edits WHERE kind='album-link' AND entity_id=$catalog AND field=CAST($id AS TEXT)",
            ("tag","video")=>"""
                SELECT operation_id,server_version FROM video_owned_metadata_edits
                WHERE content_id=(SELECT content_id FROM sync_video_owners WHERE video_id=$id)
                    AND owner_key=(SELECT owner_key FROM sync_video_owners WHERE video_id=$id)
                    AND kind='video-tag-link' AND field=CAST($catalog AS TEXT)
                """,
            ("person","image")=>"""
                SELECT operation_id,server_version FROM metadata_edits WHERE kind='person' AND entity_id=$catalog
                UNION SELECT e.operation_id,e.server_version FROM detected_faces f JOIN metadata_edits e ON e.entity_id=f.id
                    AND e.kind IN ('face-state','face-rejection') WHERE f.image_id=$id
                """,
            ("person","video")=>"""
                SELECT operation_id,server_version FROM metadata_edits WHERE kind='person' AND entity_id=$catalog
                UNION SELECT e.operation_id,e.server_version FROM video_effective_faces f JOIN metadata_edits e ON e.entity_id=f.id
                    AND e.kind IN ('video-face-state','video-face-rejection') WHERE f.reference_id=$id
                """,
            _=>throw new ArgumentException("invalid_catalog_retirement_reference")
        };
        using var command=Command($"""
            SELECT e.operation_id,e.server_version,
                EXISTS(SELECT 1 FROM sync_imported_metadata_edits imported
                    WHERE imported.publisher_id=$publisher AND imported.operation_id=e.operation_id),
                EXISTS(SELECT 1 FROM sync_publishers p WHERE p.publisher_id=$publisher AND p.state='publishing')
            FROM ({sql}) e
            """);
        if(transaction is not null) command.Transaction=transaction;
        command.Parameters.AddWithValue("$id",mediaId);command.Parameters.AddWithValue("$catalog",catalogId);
        command.Parameters.AddWithValue("$publisher",(object?)publisher??DBNull.Value);
        var operations=new List<string>();
        using var rows=command.ExecuteReader();
        while(rows.Read())
        {
            var operation=rows.GetString(0);
            if(rows.GetBoolean(3) && rows.GetInt64(1)==0 && !rows.GetBoolean(2))
                operation=Picer.Core.Identity.DeviceAuthorization.Digest(publisher+":"+operation)[..32];
            operations.Add(operation);
        }
        return "v2:"+Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            operations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()))).ToLowerInvariant();
    }

    private void CaptureCatalogRetirementBaselines(string kind,long catalogId,IReadOnlyList<string> operations,SqliteTransaction transaction)
    {
        var capturedOperations=JsonSerializer.Serialize(operations);
        while(true)
        {
            var anchors=new List<(string Operation,string Media,long Id,string Global,string Publisher)>();
            using(var query=Command("""
                SELECT m.operation_id,m.media_kind,m.media_id,m.media_global_id,r.publisher_id
                FROM sync_catalog_retirement_media m JOIN sync_catalog_retirements r ON r.operation_id=m.operation_id
                WHERE r.kind=$kind AND r.catalog_id=$id AND r.state='pending'
                    AND r.operation_id IN (SELECT value FROM json_each($operations))
                    AND NOT EXISTS(SELECT 1 FROM sync_catalog_retirement_baselines b
                        WHERE b.operation_id=m.operation_id AND b.media_kind=m.media_kind AND b.media_global_id=m.media_global_id)
                    AND NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1)
                ORDER BY m.operation_id,m.media_kind,m.media_global_id LIMIT 400
                """))
            {
                query.Transaction=transaction;query.Parameters.AddWithValue("$kind",kind);query.Parameters.AddWithValue("$id",catalogId);
                query.Parameters.AddWithValue("$operations",capturedOperations);
                using var rows=query.ExecuteReader();while(rows.Read()) anchors.Add((rows.GetString(0),rows.GetString(1),rows.GetInt64(2),rows.GetString(3),rows.GetString(4)));
            }
            if(anchors.Count==0) break;
            foreach(var anchor in anchors)
            {
                var fingerprint=GetCatalogRetirementFingerprint(kind,catalogId,anchor.Media,anchor.Id,transaction,anchor.Publisher);
                using var insert=Command("INSERT INTO sync_catalog_retirement_baselines VALUES($operation,$media,$global,$fingerprint)");
                insert.Transaction=transaction;insert.Parameters.AddWithValue("$operation",anchor.Operation);
                insert.Parameters.AddWithValue("$media",anchor.Media);insert.Parameters.AddWithValue("$global",anchor.Global);
                insert.Parameters.AddWithValue("$fingerprint",fingerprint);insert.ExecuteNonQuery();
            }
        }
    }
}
