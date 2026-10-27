using System.Globalization;
using Picer.Core.Data;
using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncReceiver
{
    private Dictionary<(string Kind,long LocalId),string>? _catalogBatchReferences;

    private Dictionary<(string Kind,long LocalId),string>? ValidateCatalogReferences(SpaceSyncBatch batch)
    {
        var required=SpaceCatalogReferences.Required(batch);
        if(batch.CatalogReferences is null)
        {
            if(batch.Kind is "catalog" or "metadata" or "metadata-edits" && Scalar("""
                SELECT 1 FROM sync_retired_catalog_identities r WHERE r.publisher_id=$publisher
                    AND NOT EXISTS(SELECT 1 FROM sync_entity_identities e WHERE e.publisher_id=r.publisher_id
                        AND e.kind=r.kind AND e.global_id=r.global_id) LIMIT 1
                """,("$publisher",batch.PublisherId)) is not null)
                throw new ArgumentException("catalog_reference_proof_required");
            return null;
        }
        if(batch.CatalogReferences.Count>4000) throw new ArgumentException("too_many_catalog_references");
        var result=new Dictionary<(string Kind,long LocalId),string>();
        foreach(var reference in batch.CatalogReferences)
        {
            if(reference is null || reference.Kind is not ("tag" or "album" or "person") || reference.LocalId<=0
                || !Guid.TryParseExact(reference.GlobalId,"N",out _) || !result.TryAdd((reference.Kind,reference.LocalId),reference.GlobalId))
                throw new ArgumentException("invalid_catalog_reference");
        }
        if(!required.ToHashSet().SetEquals(result.Keys))
            throw new ArgumentException("catalog_reference_proof_required");
        foreach(var identity in batch.Identities.Where(identity=>identity is not null && identity.Kind is "tag" or "album" or "person"))
            if(!result.TryGetValue((identity.Kind,identity.LocalId),out var expected) || expected!=identity.GlobalId)
                throw new ArgumentException("catalog_reference_identity_mismatch");
        return result;
    }

    private bool IsRetiredCatalogReference(string publisher,string kind,long localId)
    {
        if(_catalogBatchReferences is not null && _catalogBatchReferences.TryGetValue((kind,localId),out var global))
            return database.IsRetiredCatalogIdentity(publisher,kind,global);
        return Scalar("""
        SELECT 1 FROM sync_entity_identities e JOIN sync_retired_catalog_identities r
            ON r.publisher_id=e.publisher_id AND r.kind=e.kind AND r.global_id=e.global_id
        WHERE e.publisher_id=$publisher AND e.kind=$kind AND e.local_key=$local
        """,("$publisher",publisher),("$kind",kind),("$local",localId.ToString(CultureInfo.InvariantCulture))) is not null;
    }

    private bool ReferencesRetiredCatalog(string publisher,MetadataEdit edit)
    {
        PicerDatabase.ValidateMetadataEdit(edit);
        return edit.Kind switch
        {
            "person"=>IsRetiredCatalogReference(publisher,"person",edit.EntityId),
            "album-link"=>IsRetiredCatalogReference(publisher,"album",edit.EntityId),
            "tag-link" or "video-tag-link"=>IsRetiredCatalogReference(publisher,"tag",long.Parse(edit.Field,CultureInfo.InvariantCulture)),
            "face-rejection" or "video-face-rejection"=>IsRetiredCatalogReference(publisher,"person",long.Parse(edit.Field,CultureInfo.InvariantCulture)),
            "face-state" or "video-face-state"=>PicerDatabase.ParseFaceManualState(edit.Value).PersonId is { } person
                && IsRetiredCatalogReference(publisher,"person",person),
            _=>false
        };
    }

    private string? FrozenCatalogForEdit(MetadataEdit edit)
    {
        if(_catalogBatchReferences is null) return null;
        (string Kind,long LocalId)? key=edit.Kind switch
        {
            "person"=>("person",edit.EntityId),
            "album-link"=>("album",edit.EntityId),
            "tag-link" or "video-tag-link"=>("tag",long.Parse(edit.Field,CultureInfo.InvariantCulture)),
            "face-rejection" or "video-face-rejection"=>("person",long.Parse(edit.Field,CultureInfo.InvariantCulture)),
            "face-state" or "video-face-state" when PicerDatabase.ParseFaceManualState(edit.Value).PersonId is { } person=>("person",person),
            _=>null
        };
        return key is { } reference?_catalogBatchReferences.GetValueOrDefault(reference):null;
    }
}
