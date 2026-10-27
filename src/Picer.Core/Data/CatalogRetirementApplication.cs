using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Identity;

namespace Picer.Core.Data;

public sealed record CatalogRetirementApplyResult(string State,string CurrentFingerprint);

public sealed partial class PicerDatabase
{
    /// <summary>Apply an authorized association. Catalogue finalization,
    /// network delivery and retired-identity replay guards are separate steps.</summary>
    public CatalogRetirementApplyResult ApplyCatalogRetirementAssociation(string publisher,
        CatalogRetirementAnchor anchor,SpaceUploadScope scope,SqliteTransaction transaction)
    {
        var (media,catalog,direction)=ResolveCatalogRetirementIdentity(publisher,anchor,scope,transaction);
        var hash=CatalogRetirementRequestHash(anchor);
        var retryScope=false;
        using(var prior=Command("""
            SELECT request_hash,state,current_fingerprint FROM sync_catalog_retirement_receipts
            WHERE publisher_id=$publisher AND operation_id=$operation AND media_kind=$mediaKind AND media_global_id=$media
            """))
        {
            prior.Transaction=transaction;prior.Parameters.AddWithValue("$publisher",publisher);
            prior.Parameters.AddWithValue("$operation",anchor.OperationId);prior.Parameters.AddWithValue("$media",anchor.MediaGlobalId);
            prior.Parameters.AddWithValue("$mediaKind",anchor.MediaKind);
            using var row=prior.ExecuteReader();
            if(row.Read())
            {
                if(row.GetString(0)!=hash) throw new InvalidDataException("catalog_retirement_payload_changed");
                var priorState=row.GetString(1);var priorFingerprint=row.GetString(2);
                row.Close();
                if(priorState=="delivered") return new("applied",priorFingerprint);
                if(priorState=="retained") return new("kept",priorFingerprint);
                if(priorState!="scope_conflict" || anchor.MediaKind!="video"
                    || !CanRetireVideoContentRelationships(publisher,media,scope,transaction)) return new(priorState,priorFingerprint);
                retryScope=true;
            }
        }
        if(retryScope)
        {
            using var retry=Command("""
                DELETE FROM sync_catalog_retirement_receipts WHERE publisher_id=$publisher AND operation_id=$operation
                    AND media_kind=$mediaKind AND media_global_id=$media AND state='scope_conflict'
                """);
            retry.Transaction=transaction;retry.Parameters.AddWithValue("$publisher",publisher);
            retry.Parameters.AddWithValue("$operation",anchor.OperationId);retry.Parameters.AddWithValue("$mediaKind",anchor.MediaKind);
            retry.Parameters.AddWithValue("$media",anchor.MediaGlobalId);retry.ExecuteNonQuery();
        }
        var current=GetCatalogRetirementFingerprint(anchor.Kind,catalog,anchor.MediaKind,media,transaction,publisher);
        var present=HasCatalogRetirementRelationship(anchor.Kind,catalog,anchor.MediaKind,media,transaction);
        var state=!present?"applied":anchor.Fingerprint is null || anchor.Fingerprint.StartsWith("v1:",StringComparison.Ordinal)?"baseline_required":anchor.Fingerprint!=current?"conflict"
            :anchor.MediaKind=="video" && !CanRetireVideoContentRelationships(publisher,media,scope,transaction)?"scope_conflict":"applied";
        IReadOnlyList<MetadataEdit> edits=Array.Empty<MetadataEdit>();
        if(state=="applied" && present)
        {
            edits=RemoveCatalogRetirementRelationships(anchor.Kind,catalog,anchor.MediaKind,media,transaction);
            RegisterCatalogRetirementEdits(publisher,direction,edits,transaction);
        }
        using(var receipt=Command("""
            INSERT INTO sync_catalog_retirement_receipts
                (publisher_id,operation_id,kind,global_id,media_kind,media_global_id,media_id,catalog_id,
                    request_hash,state,current_fingerprint,created_utc,applied_operations) VALUES
                ($publisher,$operation,$kind,$catalogGlobal,$mediaKind,$mediaGlobal,$media,$catalog,$hash,$state,$current,$now,$edits)
            """))
        {
            receipt.Transaction=transaction;receipt.Parameters.AddWithValue("$publisher",publisher);
            receipt.Parameters.AddWithValue("$operation",anchor.OperationId);receipt.Parameters.AddWithValue("$kind",anchor.Kind);
            receipt.Parameters.AddWithValue("$catalogGlobal",anchor.GlobalId);receipt.Parameters.AddWithValue("$mediaGlobal",anchor.MediaGlobalId);
            receipt.Parameters.AddWithValue("$mediaKind",anchor.MediaKind);
            receipt.Parameters.AddWithValue("$media",media);receipt.Parameters.AddWithValue("$catalog",catalog);
            receipt.Parameters.AddWithValue("$hash",hash);receipt.Parameters.AddWithValue("$state",state);
            receipt.Parameters.AddWithValue("$edits",JsonSerializer.Serialize(edits.Select(edit=>edit.OperationId)));
            receipt.Parameters.AddWithValue("$current",current);receipt.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));receipt.ExecuteNonQuery();
        }
        return new(state,current);
    }
}
