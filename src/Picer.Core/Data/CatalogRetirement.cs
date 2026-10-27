using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    /// <summary>Capture authorized identity anchors before relationship deletion or a person merge.
    /// The queue contains identifiers only; it is not an acknowledgement of remote deletion.</summary>
    public IReadOnlyList<string> RecordCatalogRetirement(string kind,long catalogId,SqliteTransaction transaction)
    {
        var relationships=CatalogRetirementRelationshipsSql(kind);
        var operations=new List<string>();
        using(var create=Command("""
            INSERT INTO sync_catalog_retirements(operation_id,publisher_id,kind,catalog_id,global_id,created_utc)
            SELECT lower(hex(randomblob(16))),e.publisher_id,e.kind,$id,e.global_id,$now
            FROM sync_active_entity_identities e JOIN sync_publishers p ON p.publisher_id=e.publisher_id
            WHERE e.kind=$kind AND ((p.state='receiving' AND e.target_id=$id)
                OR (p.state='publishing' AND e.local_key=CAST($id AS TEXT)))
                AND NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1)
            ON CONFLICT(publisher_id,kind,global_id) DO NOTHING RETURNING operation_id;
            """))
        {
            create.Transaction=transaction;create.Parameters.AddWithValue("$kind",kind);create.Parameters.AddWithValue("$id",catalogId);
            create.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));
            using var rows=create.ExecuteReader();while(rows.Read()) operations.Add(rows.GetString(0));
        }
        if(operations.Count==0) return operations;
        using var command=Command($"""
            INSERT INTO sync_catalog_retirement_media(operation_id,media_kind,media_id,source_id,media_global_id)
            SELECT r.operation_id,owned.media_kind,owned.media_id,
                CASE owned.media_kind WHEN 'image' THEN (SELECT source_id FROM images WHERE id=owned.media_id)
                    ELSE (SELECT source_id FROM videos WHERE id=owned.media_id) END,e.global_id
            FROM ({relationships}) owned
            JOIN sync_catalog_retirements r ON r.kind=$kind AND r.catalog_id=$id AND r.state='pending'
            JOIN sync_publishers p ON p.publisher_id=r.publisher_id
            JOIN sync_entity_identities e ON e.publisher_id=r.publisher_id AND e.kind=owned.media_kind
                AND ((p.state='receiving' AND e.target_id=owned.media_id)
                    OR (p.state='publishing' AND e.local_key=CAST(owned.media_id AS TEXT)))
            WHERE r.operation_id IN (SELECT value FROM json_each($operations))
                AND NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1)
                AND (p.state<>'receiving' OR EXISTS(SELECT 1 FROM sync_source_bindings b
                    WHERE b.publisher_id=r.publisher_id AND b.source_id=CASE owned.media_kind
                        WHEN 'image' THEN (SELECT source_id FROM images WHERE id=owned.media_id)
                        ELSE (SELECT source_id FROM videos WHERE id=owned.media_id) END))
            ON CONFLICT(operation_id,media_kind,media_global_id) DO NOTHING;
            """);
        command.Transaction=transaction;command.Parameters.AddWithValue("$kind",kind);command.Parameters.AddWithValue("$id",catalogId);
        command.Parameters.AddWithValue("$operations",System.Text.Json.JsonSerializer.Serialize(operations));command.ExecuteNonQuery();
        CaptureCatalogRetirementBaselines(kind,catalogId,operations,transaction);
        return operations;
    }

    /// <summary>Seal only newly captured operations at the end of their business transaction.
    /// The source must publish this cut before it can send the associated retirement.</summary>
    public void SealCatalogRetirements(IReadOnlyList<string> operations,SqliteTransaction transaction)
    {
        if(operations.Count==0) return;
        using var seal=Command("""
            UPDATE sync_catalog_retirements SET
                sealed_metadata_revision=(SELECT metadata_revision FROM mirror_revision WHERE id=1),
                captured_media_count=(SELECT COUNT(*) FROM sync_catalog_retirement_media m
                    WHERE m.operation_id=sync_catalog_retirements.operation_id)
            WHERE operation_id IN(SELECT value FROM json_each($operations)) AND sealed_metadata_revision IS NULL;
            """);
        seal.Transaction=transaction;
        seal.Parameters.AddWithValue("$operations",System.Text.Json.JsonSerializer.Serialize(operations));
        seal.ExecuteNonQuery();
    }
}
