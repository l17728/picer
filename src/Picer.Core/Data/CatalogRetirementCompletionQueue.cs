using System.Text.Json;
using Picer.Core.Identity;

namespace Picer.Core.Data;

public sealed record CatalogRetirementManifest(string OperationId,string Kind,string GlobalId,long MediaCount);

public sealed partial class PicerDatabase
{
    /// <summary>Finalization is a separate bounded queue, including genuinely empty
    /// catalog objects. Paused or unacknowledged media never disappear from this barrier.</summary>
    public IReadOnlyList<CatalogRetirementManifest> GetReadyCatalogRetirements(string publisher,SpaceUploadScope scope,
        string after="",int limit=100)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if(limit is <1 or >200 || after is null || after.Length>32) throw new ArgumentException("invalid_catalog_completion_cursor");
        using var query=Command("""
            SELECT r.operation_id,r.kind,r.global_id,r.captured_media_count
            FROM sync_catalog_retirements r JOIN sync_publishers p ON p.publisher_id=r.publisher_id
            WHERE r.publisher_id=$publisher AND r.state='pending' AND p.state IN ('publishing','receiving')
                AND r.operation_id>$after AND r.sealed_metadata_revision IS NOT NULL AND r.captured_media_count IS NOT NULL
                AND (p.state='receiving' OR p.uploaded_metadata_revision>=r.sealed_metadata_revision)
                AND ($photos=1 OR $videos=1) AND (r.kind<>'person' OR $people=1) AND (r.kind<>'album' OR $photos=1)
                AND EXISTS(SELECT 1 FROM sources s WHERE s.id IN(SELECT value FROM json_each($sources))
                    AND (p.state='publishing' OR EXISTS(SELECT 1 FROM sync_source_bindings binding
                        WHERE binding.publisher_id=p.publisher_id AND binding.source_id=s.id)))
                AND EXISTS(SELECT 1 FROM sync_entity_identities identity
                    WHERE identity.publisher_id=r.publisher_id AND identity.kind=r.kind AND identity.global_id=r.global_id
                        AND CASE p.state WHEN 'receiving' THEN identity.target_id ELSE CAST(identity.local_key AS INTEGER) END=r.catalog_id)
                AND NOT EXISTS(SELECT 1 FROM sync_catalog_retirement_media m WHERE m.operation_id=r.operation_id AND (
                    m.source_id NOT IN(SELECT value FROM json_each($sources))
                    OR (m.media_kind='image' AND $photos=0) OR (m.media_kind='video' AND $videos=0)
                    OR (p.state='receiving' AND NOT EXISTS(SELECT 1 FROM sync_source_bindings binding
                        WHERE binding.publisher_id=p.publisher_id AND binding.source_id=m.source_id))
                    OR NOT EXISTS(SELECT 1 FROM sync_catalog_retirement_receipts receipt
                        WHERE receipt.publisher_id=r.publisher_id AND receipt.operation_id=r.operation_id
                            AND receipt.media_kind=m.media_kind AND receipt.media_global_id=m.media_global_id AND receipt.state IN ('delivered','retained'))))
            ORDER BY r.operation_id LIMIT $limit
            """);
        query.Parameters.AddWithValue("$publisher",publisher);query.Parameters.AddWithValue("$after",after);
        query.Parameters.AddWithValue("$limit",limit);query.Parameters.AddWithValue("$photos",scope.Photos?1:0);
        query.Parameters.AddWithValue("$videos",scope.Videos?1:0);query.Parameters.AddWithValue("$people",scope.People?1:0);
        query.Parameters.AddWithValue("$sources",JsonSerializer.Serialize(scope.SourceIds));
        using var rows=query.ExecuteReader();var result=new List<CatalogRetirementManifest>();
        while(rows.Read()) result.Add(new(rows.GetString(0),rows.GetString(1),rows.GetString(2),rows.GetInt64(3)));
        return result;
    }
}
