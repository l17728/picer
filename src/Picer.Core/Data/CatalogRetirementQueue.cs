using System.Text.Json;
using Picer.Core.Identity;

namespace Picer.Core.Data;

public sealed record CatalogRetirementCursor(string OperationId, string MediaKind, string MediaGlobalId);
public sealed record CatalogRetirementAnchor(string OperationId, string Kind, string GlobalId,
    string MediaKind, string MediaGlobalId, long SourceId)
{
    public string? Fingerprint { get; init; }
    public CatalogRetirementCursor Cursor => new(OperationId, MediaKind, MediaGlobalId);
}

public sealed partial class PicerDatabase
{
    /// <summary>Read bounded, currently authorized anchors without acknowledging delivery.
    /// Paused roots remain pending and can be read when the caller starts a new scoped pass.</summary>
    public IReadOnlyList<CatalogRetirementAnchor> GetPendingCatalogRetirementAnchors(
        string publisherId, SpaceUploadScope scope, CatalogRetirementCursor? after = null, int limit = 400,
        bool requirePublishedCut = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherId);
        ArgumentNullException.ThrowIfNull(scope);
        if (limit is < 1 or > 400) throw new ArgumentOutOfRangeException(nameof(limit));
        using var command = Command("""
            SELECT r.operation_id,r.kind,r.global_id,m.media_kind,m.media_global_id,m.source_id,b.fingerprint
            FROM sync_catalog_retirements r
            JOIN sync_catalog_retirement_media m ON m.operation_id=r.operation_id
            JOIN sync_publishers p ON p.publisher_id=r.publisher_id
            LEFT JOIN sync_catalog_retirement_baselines b ON b.operation_id=m.operation_id
                AND b.media_kind=m.media_kind AND b.media_global_id=m.media_global_id
            WHERE r.publisher_id=$publisher AND r.state='pending'
                AND NOT EXISTS(SELECT 1 FROM sync_catalog_retirement_receipts receipt
                    WHERE receipt.publisher_id=r.publisher_id AND receipt.operation_id=r.operation_id
                        AND receipt.media_kind=m.media_kind AND receipt.media_global_id=m.media_global_id AND receipt.state IN ('delivered','retained'))
                AND p.state IN ('publishing','receiving')
                AND ($requireCut=0 OR (r.sealed_metadata_revision IS NOT NULL
                    AND (p.state='receiving' OR p.uploaded_metadata_revision>=r.sealed_metadata_revision)))
                AND (r.kind<>'person' OR $people=1)
                AND ((m.media_kind='image' AND $photos=1) OR (m.media_kind='video' AND $videos=1))
                AND m.source_id IN (SELECT value FROM json_each($sources))
                AND (r.operation_id,m.media_kind,m.media_global_id)>($operation,$mediaKind,$mediaGlobal)
                AND EXISTS(SELECT 1 FROM sync_entity_identities c
                    WHERE c.publisher_id=r.publisher_id AND c.kind=r.kind AND c.global_id=r.global_id
                        AND ((p.state='receiving' AND c.target_id=r.catalog_id)
                            OR (p.state='publishing' AND c.local_key=CAST(r.catalog_id AS TEXT))))
                AND EXISTS(SELECT 1 FROM sync_entity_identities e
                    WHERE e.publisher_id=r.publisher_id AND e.kind=m.media_kind AND e.global_id=m.media_global_id
                        AND ((p.state='receiving' AND e.target_id=m.media_id)
                            OR (p.state='publishing' AND e.local_key=CAST(m.media_id AS TEXT))))
                AND ((m.media_kind='image' AND EXISTS(SELECT 1 FROM images i WHERE i.id=m.media_id AND i.source_id=m.source_id))
                    OR (m.media_kind='video' AND EXISTS(SELECT 1 FROM videos v WHERE v.id=m.media_id AND v.source_id=m.source_id)))
                AND (p.state<>'receiving' OR EXISTS(SELECT 1 FROM sync_source_bindings b
                    WHERE b.publisher_id=r.publisher_id AND b.source_id=m.source_id))
            ORDER BY r.operation_id,m.media_kind,m.media_global_id LIMIT $limit
            """);
        command.Parameters.AddWithValue("$publisher", publisherId);
        command.Parameters.AddWithValue("$requireCut", requirePublishedCut ? 1 : 0);
        command.Parameters.AddWithValue("$people", scope.People ? 1 : 0);
        command.Parameters.AddWithValue("$photos", scope.Photos ? 1 : 0);
        command.Parameters.AddWithValue("$videos", scope.Videos ? 1 : 0);
        command.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(scope.SourceIds));
        command.Parameters.AddWithValue("$operation", after?.OperationId ?? "");
        command.Parameters.AddWithValue("$mediaKind", after?.MediaKind ?? "");
        command.Parameters.AddWithValue("$mediaGlobal", after?.MediaGlobalId ?? "");
        command.Parameters.AddWithValue("$limit", limit);
        using var rows = command.ExecuteReader();
        var result = new List<CatalogRetirementAnchor>();
        while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2),
            rows.GetString(3), rows.GetString(4), rows.GetInt64(5)) { Fingerprint=rows.IsDBNull(6)?null:rows.GetString(6) });
        return result;
    }
}
