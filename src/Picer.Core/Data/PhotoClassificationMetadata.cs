using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    public void RecordPhotoClassificationDecision(long imageId,string concept,string value,bool confirmation,SqliteTransaction transaction)
    {
        var edit=RecordMetadataEditCore(confirmation?"photo-classification-confirmation":"photo-classification-policy",imageId,concept,value,transaction);
        ApplyPhotoClassificationEdit(edit,transaction);
    }

    public void ReplayPhotoClassificationFields(long imageId,SqliteTransaction transaction)
    {
        var identity=GetPhotoClassificationContentIdentity(imageId,transaction);
        var edits=new List<MetadataEdit>();
        using(var query=Command("""
            SELECT e.kind,e.entity_id,e.field,e.value,e.updated_utc,e.operation_id,e.server_version,e.base_version
            FROM photo_metadata_operation_bindings b JOIN metadata_edits e ON e.operation_id=b.operation_id
            WHERE b.owner_key=(SELECT owner_key FROM sync_image_owners WHERE image_id=$image) AND b.content_identity=$identity
                AND e.kind IN ('photo-classification-confirmation','photo-classification-policy')
            """))
        {
            query.Transaction=transaction;query.Parameters.AddWithValue("$image",imageId);query.Parameters.AddWithValue("$identity",identity);
            using var rows=query.ExecuteReader();
            while(rows.Read()) edits.Add(new(rows.GetString(0),rows.GetInt64(1),rows.GetString(2),rows.GetString(3),rows.GetString(4),rows.GetString(5))
                { ServerVersion=rows.GetInt64(6),BaseVersion=rows.GetInt64(7),PhotoContentIdentity=identity });
        }
        foreach(var edit in EffectiveMetadataEdits(edits,transaction)) ApplyPhotoClassificationEdit(edit,transaction);
    }
    private static bool IsPhotoClassificationEdit(MetadataEdit edit)=>
        edit.Kind is "photo-classification-confirmation" or "photo-classification-policy";

    public IReadOnlyList<MetadataEdit> GetPendingPhotoClassificationProposals()
    {
        var proposals=new List<MetadataEdit>();
        using(var query=Command("""
            SELECT proposed_json FROM metadata_edit_conflicts
            WHERE state='pending' AND kind IN ('photo-classification-confirmation','photo-classification-policy')
            ORDER BY rowid
            """))
        {
            using var rows=query.ExecuteReader();
            while(rows.Read()) proposals.Add(System.Text.Json.JsonSerializer.Deserialize<MetadataEdit>(rows.GetString(0))!);
        }
        return proposals.Where(edit=>edit.ServerVersion==0 && ResolvePhotoClassificationOperation(edit.OperationId) is not null)
            .Select(edit=>NormalizeCausalPhotoEdit(edit,null)).ToArray();
    }

    private void PreserveDisplacedPhotoClassification(MetadataEdit incoming,SqliteTransaction? transaction)
    {
        MetadataEdit? previous=null;
        using(var query=Command("""
            SELECT value,updated_utc,operation_id,server_version,base_version FROM metadata_edits
            WHERE kind=$kind AND entity_id=$image AND field=$field
            """))
        {
            if(transaction is not null) query.Transaction=transaction;
            query.Parameters.AddWithValue("$kind",incoming.Kind);query.Parameters.AddWithValue("$image",incoming.EntityId);
            query.Parameters.AddWithValue("$field",incoming.Field);
            using var row=query.ExecuteReader();
            if(row.Read()) previous=new(incoming.Kind,incoming.EntityId,incoming.Field,row.GetString(0),row.GetString(1),row.GetString(2))
                { ServerVersion=row.GetInt64(3),BaseVersion=row.GetInt64(4) };
        }
        if(previous is null || previous.OperationId==incoming.OperationId) return;
        // A reused reference may still store an operation for its former bytes.
        // Move that operation to its surviving copy before reusing the field slot.
        if(ResolvePhotoClassificationOperation(previous.OperationId,transaction) is not { } survivor || survivor==incoming.EntityId) return;
        previous=NormalizeCausalPhotoEdit(previous,transaction);
        using(var release=Command("DELETE FROM metadata_edits WHERE kind=$kind AND entity_id=$image AND field=$field AND operation_id=$operation"))
        {
            if(transaction is not null) release.Transaction=transaction;
            release.Parameters.AddWithValue("$kind",incoming.Kind);release.Parameters.AddWithValue("$image",incoming.EntityId);
            release.Parameters.AddWithValue("$field",incoming.Field);release.Parameters.AddWithValue("$operation",previous.OperationId);
            release.ExecuteNonQuery();
        }
        SaveMetadataEdit(previous,transaction);
    }

    private MetadataEdit BindPhotoMetadataIdentity(MetadataEdit edit,SqliteTransaction? transaction)
    {
        if(!IsPhotoClassificationEdit(edit)) return edit;
        if(edit.PhotoContentIdentity?.StartsWith("sha256:",StringComparison.Ordinal)==true)
            edit=edit with { PhotoContentIdentity=edit.PhotoContentIdentity.ToLowerInvariant() };
        using var owned=transaction is null?BeginTransaction():null;
        var active=transaction??owned!;
        string? previous;
        using(var observed=Command("SELECT content_identity FROM photo_metadata_operation_bindings WHERE operation_id=$operation"))
        {
            observed.Transaction=active;observed.Parameters.AddWithValue("$operation",edit.OperationId);
            previous=observed.ExecuteScalar() as string;
        }
        if(previous is not null)
        {
            using var exists=Command("SELECT 1 FROM images WHERE id=$id");exists.Transaction=active;
            exists.Parameters.AddWithValue("$id",edit.EntityId);
            if(exists.ExecuteScalar() is null) edit=NormalizeCausalPhotoEdit(edit,active);
        }
        if(previous is null && edit.PhotoContentIdentity is { } supplied && supplied!=GetPhotoClassificationContentIdentity(edit.EntityId,active)
            && supplied!=GetLocalPhotoContentIdentity(edit.EntityId,active))
        {
            using var equivalent=Command("""
                SELECT MIN(i.id) FROM images i JOIN sync_image_owners o ON o.image_id=i.id
                WHERE 'sha256:'||lower(i.sha256)=$identity
                    AND o.owner_key=(SELECT owner_key FROM sync_image_owners WHERE image_id=$origin)
                """);
            equivalent.Transaction=active;equivalent.Parameters.AddWithValue("$identity",supplied);equivalent.Parameters.AddWithValue("$origin",edit.EntityId);
            if(equivalent.ExecuteScalar() is long reference) edit=edit with { EntityId=reference };
            else
            {
                // A delayed operation cannot acquire the current file merely because its reference ID survived.
                owned?.Commit();return edit;
            }
        }
        var identity=BindPhotoClassificationOperation(edit.OperationId,edit.EntityId,active);
        owned?.Commit();return edit with { PhotoContentIdentity=identity };
    }

    private MetadataEdit NormalizeCausalPhotoEdit(MetadataEdit edit,SqliteTransaction? transaction)
    {
        if(!IsPhotoClassificationEdit(edit)) return edit;
        using var query=Command("SELECT content_identity FROM photo_metadata_operation_bindings WHERE operation_id=$operation");
        if(transaction is not null) query.Transaction=transaction;
        query.Parameters.AddWithValue("$operation",edit.OperationId);
        if(query.ExecuteScalar() is string identity) edit=edit with { PhotoContentIdentity=identity };
        if(ResolvePhotoClassificationOperation(edit.OperationId,transaction) is { } reference) edit=edit with { EntityId=reference };
        return edit;
    }

    private (string Kind,string Entity,string Field)? PhotoMetadataContentKey(MetadataEdit edit,SqliteTransaction? transaction)
    {
        if(ResolvePhotoClassificationOperation(edit.OperationId,transaction) is null) return null;
        using var query=Command("SELECT owner_key,content_identity FROM photo_metadata_operation_bindings WHERE operation_id=$operation");
        if(transaction is not null) query.Transaction=transaction;
        query.Parameters.AddWithValue("$operation",edit.OperationId);
        using var row=query.ExecuteReader();
        return row.Read() ? (edit.Kind,"photo-content:"+row.GetString(0)+":"+row.GetString(1),edit.Field) : null;
    }

    private static bool ValidPhotoContentIdentity(string identity)
    {
        if(identity.StartsWith("sha256:",StringComparison.Ordinal)) return identity.Length==71 && identity[7..].All(Uri.IsHexDigit);
        var parts=identity.Split(':');
        return parts is { Length:3 } && parts[0]=="local" && Guid.TryParseExact(parts[1],"N",out _) && Guid.TryParseExact(parts[2],"N",out _);
    }

    private void ApplyPhotoClassificationEdit(MetadataEdit edit,SqliteTransaction transaction)
    {
        const string references="""
            SELECT i.id FROM images i JOIN sync_image_owners o ON o.image_id=i.id
            JOIN photo_metadata_file_versions v ON v.image_id=i.id
            JOIN photo_metadata_operation_bindings b ON b.operation_id=$operation
            WHERE o.owner_key=b.owner_key AND (b.content_identity='sha256:'||lower(i.sha256)
                OR (i.id=b.reference_id AND b.content_identity='local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||v.version_id))
            """;
        var normalized=NormalizeCausalPhotoEdit(edit,transaction);
        var hash=normalized.PhotoContentIdentity?.StartsWith("sha256:",StringComparison.Ordinal)==true ? normalized.PhotoContentIdentity[7..] : null;
        string sql;
        if(edit.Kind=="photo-classification-confirmation")
        {
            sql=edit.Value=="1" ? $"""
                INSERT INTO image_classification_assertions(image_id,content_sha256,concept_id,source_kind,source_version,
                    assertion_state,review_state,created_utc,updated_utc,reviewed_utc)
                SELECT id,$sha,$concept,'user','user-v1','user_confirmed','accepted',$utc,$utc,$utc FROM images
                WHERE id IN ({references})
                ON CONFLICT(image_id,concept_id,source_kind,source_version) DO UPDATE SET
                    content_sha256=excluded.content_sha256,assertion_state='user_confirmed',review_state='accepted',updated_utc=$utc,reviewed_utc=$utc;
                """ : $"""
                UPDATE image_classification_assertions SET review_state='retracted',updated_utc=$utc,reviewed_utc=$utc
                WHERE image_id IN ({references}) AND concept_id=$concept AND source_kind='user';
                """;
        }
        else if(edit.Value is "rejected" or "retracted")
        {
            sql=$"""
                DELETE FROM image_classification_rejections WHERE $sha IS NOT NULL AND image_id IN ({references})
                    AND concept_id=$concept AND source_scope IN ('vision_model','all');
                INSERT INTO image_classification_rejections(image_id,content_sha256,concept_id,source_scope,rejected_utc,owner_key)
                SELECT CASE WHEN $sha IS NULL THEN $image ELSE NULL END,$sha,$concept,'vision_model',$utc,owner_key
                FROM photo_metadata_operation_bindings WHERE operation_id=$operation
                ON CONFLICT DO UPDATE SET rejected_utc=$utc;
                INSERT INTO visual_concept_rejections(image_id,concept_id,rejected_utc)
                SELECT id,$concept,$utc FROM images WHERE id IN ({references})
                ON CONFLICT(image_id,concept_id) DO UPDATE SET rejected_utc=$utc;
                UPDATE image_classification_assertions SET content_sha256=$sha,review_state=$value,updated_utc=$utc,reviewed_utc=$utc
                WHERE image_id IN ({references}) AND concept_id=$concept AND source_kind='vision_model';
                DELETE FROM image_visual_concepts WHERE image_id IN ({references}) AND concept_id=$concept;
                """;
        }
        else
        {
            sql=$"""
                DELETE FROM image_classification_rejections WHERE concept_id=$concept AND source_scope IN ('vision_model','all')
                    AND (image_id IN ({references}) OR ($sha IS NOT NULL AND content_sha256=$sha
                        AND owner_key=(SELECT owner_key FROM photo_metadata_operation_bindings WHERE operation_id=$operation)));
                DELETE FROM visual_concept_rejections WHERE image_id IN ({references}) AND concept_id=$concept;
                UPDATE image_classification_assertions SET content_sha256=$sha,review_state=CASE WHEN $value='restored' THEN 'pending' ELSE 'accepted' END,
                    assertion_state=CASE WHEN $value='restored' THEN 'suggested' ELSE assertion_state END,
                    updated_utc=$utc,reviewed_utc=CASE WHEN $value='restored' THEN NULL ELSE $utc END
                WHERE image_id IN ({references}) AND concept_id=$concept AND source_kind='vision_model';
                """;
        }
        using var command=Command(sql);command.Transaction=transaction;
        command.Parameters.AddWithValue("$operation",edit.OperationId);command.Parameters.AddWithValue("$sha",(object?)hash??DBNull.Value);
        command.Parameters.AddWithValue("$image",normalized.EntityId);command.Parameters.AddWithValue("$concept",edit.Field);
        command.Parameters.AddWithValue("$utc",edit.UpdatedUtc);command.Parameters.AddWithValue("$value",edit.Value);
        command.ExecuteNonQuery();
    }
}
