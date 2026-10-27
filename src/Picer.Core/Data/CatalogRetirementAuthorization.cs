using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Identity;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    private static bool IsCatalogRetirementFingerprint(string? fingerprint)=>fingerprint is { Length:67 }
        && (fingerprint.StartsWith("v1:",StringComparison.Ordinal) || fingerprint.StartsWith("v2:",StringComparison.Ordinal))
        && fingerprint.AsSpan(3).IndexOfAnyExcept("0123456789abcdef")<0;

    private static void ValidateCatalogRetirementAnchor(CatalogRetirementAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if(anchor.Kind is not ("tag" or "album" or "person") || anchor.MediaKind is not ("image" or "video")
            || anchor.Kind=="album" && anchor.MediaKind=="video"
            || !Guid.TryParseExact(anchor.OperationId,"N",out _)
            || !Guid.TryParseExact(anchor.GlobalId,"N",out _) || !Guid.TryParseExact(anchor.MediaGlobalId,"N",out _))
            throw new ArgumentException("invalid_catalog_retirement_request");
        if(anchor.Fingerprint is not null && !IsCatalogRetirementFingerprint(anchor.Fingerprint))
            throw new ArgumentException("invalid_catalog_retirement_baseline");
    }

    private static string CatalogRetirementRequestHash(CatalogRetirementAnchor anchor)=>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { Version=1,anchor.OperationId,anchor.Kind,anchor.GlobalId,anchor.MediaKind,anchor.MediaGlobalId,anchor.Fingerprint })));

    private (long Media,long Catalog,string Direction) ResolveCatalogRetirementIdentity(string publisher,
        CatalogRetirementAnchor anchor,SpaceUploadScope scope,SqliteTransaction transaction)
    {
        ValidateCatalogRetirementAnchor(anchor);ArgumentNullException.ThrowIfNull(scope);
        if(anchor.Kind=="person" && !scope.People) throw new UnauthorizedAccessException("catalog_retirement_outside_scope");
        var mediaTable=anchor.MediaKind=="image"?"images":"videos";
        using var identity=Command($"""
            SELECT i.id,CASE WHEN p.state='receiving' THEN c.target_id ELSE CAST(c.local_key AS INTEGER) END,p.state
            FROM sync_publishers p
            JOIN sync_entity_identities c ON c.publisher_id=p.publisher_id AND c.kind=$kind AND c.global_id=$catalog
            JOIN sync_entity_identities m ON m.publisher_id=p.publisher_id AND m.kind=$mediaKind AND m.global_id=$media
            JOIN {mediaTable} i ON i.id=CASE WHEN p.state='receiving' THEN m.target_id ELSE CAST(m.local_key AS INTEGER) END
            WHERE p.publisher_id=$publisher AND p.state IN ('publishing','receiving')
                AND i.source_id IN (SELECT value FROM json_each($sources)) AND $enabled=1
                AND (p.state<>'receiving' OR EXISTS(SELECT 1 FROM sync_source_bindings b
                    WHERE b.publisher_id=p.publisher_id AND b.source_id=i.source_id))
            """);
        identity.Transaction=transaction;identity.Parameters.AddWithValue("$publisher",publisher);
        identity.Parameters.AddWithValue("$kind",anchor.Kind);identity.Parameters.AddWithValue("$catalog",anchor.GlobalId);
        identity.Parameters.AddWithValue("$media",anchor.MediaGlobalId);identity.Parameters.AddWithValue("$sources",JsonSerializer.Serialize(scope.SourceIds));
        identity.Parameters.AddWithValue("$mediaKind",anchor.MediaKind);
        identity.Parameters.AddWithValue("$enabled",(anchor.MediaKind=="image"?scope.Photos:scope.Videos)?1:0);
        using var row=identity.ExecuteReader();
        if(!row.Read() || row.IsDBNull(1)) throw new UnauthorizedAccessException("catalog_retirement_outside_scope");
        return (row.GetInt64(0),row.GetInt64(1),row.GetString(2));
    }
}
