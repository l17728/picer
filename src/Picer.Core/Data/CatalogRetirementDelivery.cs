using Microsoft.Data.Sqlite;
using Picer.Core.Identity;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    public bool RecordCatalogRetirementDelivery(string publisher,CatalogRetirementAnchor anchor,
        CatalogRetirementApplyResult result,SpaceUploadScope scope,SqliteTransaction transaction)
    {
        var (media,catalog,_)=ResolveCatalogRetirementIdentity(publisher,anchor,scope,transaction);
        ArgumentNullException.ThrowIfNull(result);
        if(result.State is not ("applied" or "kept" or "conflict" or "baseline_required" or "scope_conflict")
            || !IsCatalogRetirementFingerprint(result.CurrentFingerprint)) throw new ArgumentException("invalid_catalog_retirement_receipt");
        using(var origin=Command("""
            SELECT b.fingerprint FROM sync_catalog_retirements r
            JOIN sync_catalog_retirement_media m ON m.operation_id=r.operation_id
            LEFT JOIN sync_catalog_retirement_baselines b ON b.operation_id=m.operation_id
                AND b.media_kind=m.media_kind AND b.media_global_id=m.media_global_id
            WHERE r.publisher_id=$publisher AND r.operation_id=$operation AND r.kind=$kind AND r.global_id=$catalog
                AND m.media_kind=$mediaKind AND m.media_global_id=$media
            """))
        {
            origin.Transaction=transaction;origin.Parameters.AddWithValue("$publisher",publisher);
            origin.Parameters.AddWithValue("$operation",anchor.OperationId);origin.Parameters.AddWithValue("$kind",anchor.Kind);
            origin.Parameters.AddWithValue("$catalog",anchor.GlobalId);origin.Parameters.AddWithValue("$mediaKind",anchor.MediaKind);
            origin.Parameters.AddWithValue("$media",anchor.MediaGlobalId);
            using var row=origin.ExecuteReader();
            if(!row.Read() || (row.IsDBNull(0)?null:row.GetString(0))!=anchor.Fingerprint)
                throw new InvalidDataException("catalog_retirement_origin_mismatch");
        }
        var hash=CatalogRetirementRequestHash(anchor);
        using(var prior=Command("""
            SELECT request_hash FROM sync_catalog_retirement_receipts
            WHERE publisher_id=$publisher AND operation_id=$operation AND media_kind=$mediaKind AND media_global_id=$media
            """))
        {
            prior.Transaction=transaction;prior.Parameters.AddWithValue("$publisher",publisher);
            prior.Parameters.AddWithValue("$operation",anchor.OperationId);prior.Parameters.AddWithValue("$mediaKind",anchor.MediaKind);
            prior.Parameters.AddWithValue("$media",anchor.MediaGlobalId);
            if(prior.ExecuteScalar() is string existing && existing!=hash) throw new InvalidDataException("catalog_retirement_payload_changed");
        }
        using var receipt=Command("""
            INSERT INTO sync_catalog_retirement_receipts
                (publisher_id,operation_id,kind,global_id,media_kind,media_global_id,media_id,catalog_id,
                    request_hash,state,current_fingerprint,created_utc) VALUES
                ($publisher,$operation,$kind,$catalogGlobal,$mediaKind,$mediaGlobal,$media,$catalog,$hash,$state,$fingerprint,$now)
            ON CONFLICT(publisher_id,operation_id,media_kind,media_global_id) DO UPDATE SET
                state=excluded.state,current_fingerprint=excluded.current_fingerprint
            WHERE sync_catalog_retirement_receipts.state NOT IN ('delivered','retained')
                AND (sync_catalog_retirement_receipts.state<>excluded.state
                    OR sync_catalog_retirement_receipts.current_fingerprint<>excluded.current_fingerprint)
            """);
        receipt.Transaction=transaction;receipt.Parameters.AddWithValue("$publisher",publisher);
        receipt.Parameters.AddWithValue("$operation",anchor.OperationId);receipt.Parameters.AddWithValue("$kind",anchor.Kind);
        receipt.Parameters.AddWithValue("$catalogGlobal",anchor.GlobalId);receipt.Parameters.AddWithValue("$mediaKind",anchor.MediaKind);
        receipt.Parameters.AddWithValue("$mediaGlobal",anchor.MediaGlobalId);receipt.Parameters.AddWithValue("$media",media);
        receipt.Parameters.AddWithValue("$catalog",catalog);receipt.Parameters.AddWithValue("$hash",hash);
        receipt.Parameters.AddWithValue("$state",result.State switch { "applied"=>"delivered","kept"=>"retained",_=>result.State });
        receipt.Parameters.AddWithValue("$fingerprint",result.CurrentFingerprint);receipt.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));
        return receipt.ExecuteNonQuery()>0;
    }
}
