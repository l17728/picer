using System.Text.Json;

namespace Picer.Core.Data;

public sealed record VideoOwnershipCandidate(long VideoId,string Path,long Version,string? CurrentOperationId,string? CurrentValue,string? SourceName=null);
public sealed record VideoOwnershipReview(string OperationId,string Kind,string Field,string Value,IReadOnlyList<VideoOwnershipCandidate> Candidates,string? Description=null,string? Epoch=null);
public sealed record VideoOwnershipChoice(long VideoId,long Version,string? CurrentOperationId);
public sealed record VideoOwnershipResolution(string OperationId,IReadOnlyList<VideoOwnershipChoice> Choices,bool Discard=false,string? Epoch=null);

public sealed partial class PicerDatabase
{
    internal void QuarantineVideoMetadataOperation(string operationId, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        MetadataEdit? edit = null;
        using (var query = Command("SELECT kind,entity_id,field,value,updated_utc,operation_id,video_content_identity,server_version,base_version FROM metadata_edits WHERE operation_id=$op"))
        {
            query.Transaction = transaction; query.Parameters.AddWithValue("$op", operationId);
            using var row = query.ExecuteReader();
            if (row.Read()) edit = new(row.GetString(0), row.GetInt64(1), row.GetString(2), row.GetString(3), row.GetString(4), row.GetString(5))
                { VideoContentIdentity = row.IsDBNull(6) ? null : row.GetString(6), ServerVersion = row.GetInt64(7), BaseVersion = row.GetInt64(8) };
        }
        if (edit is null || !IsVideoMetadataEdit(edit)) return;
        var target = ResolveVideoEditTarget(edit, transaction);
        if (target is null) return;
        var contentId = target.Value.ContentId;
        using var archive = Command("INSERT OR IGNORE INTO video_metadata_ownership_reviews(operation_id,content_id,kind,field,payload) VALUES($op,$content,$kind,$field,$payload)");
        archive.Transaction = transaction;
        archive.Parameters.AddWithValue("$op", edit.OperationId); archive.Parameters.AddWithValue("$content", contentId);
        archive.Parameters.AddWithValue("$kind", edit.Kind); archive.Parameters.AddWithValue("$field", edit.Field);
        archive.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(edit)); archive.ExecuteNonQuery();
        // Remove only this source's compatibility projection. Another owner's
        // content projection is never authority for this retired operation.
        using var legacyOwner = Command("SELECT 1 FROM video_legacy_metadata_owners legacy JOIN sync_video_owners owner ON owner.owner_key=legacy.owner_key WHERE legacy.content_id=$content AND owner.video_id=$id");
        legacyOwner.Transaction = transaction; legacyOwner.Parameters.AddWithValue("$content", contentId);
        legacyOwner.Parameters.AddWithValue("$id", target.Value.ReferenceId);
        if (CanWriteLegacyVideoMetadata(target.Value.ReferenceId, transaction) || legacyOwner.ExecuteScalar() is not null)
        {
            using var clear = Command(edit.Kind == "video-tag-link"
                ? "DELETE FROM video_tags WHERE video_id=$content AND tag_id=CAST($field AS INTEGER)"
                : "DELETE FROM video_visual_concepts WHERE video_id=$content AND concept_id=json_extract($field,'$[0]') AND model_version=json_extract($field,'$[1]')");
            clear.Transaction = transaction; clear.Parameters.AddWithValue("$content", contentId);
            clear.Parameters.AddWithValue("$field", edit.Field); clear.ExecuteNonQuery();
        }
        using var remove = Command("DELETE FROM metadata_edits WHERE operation_id=$op");
        remove.Transaction = transaction; remove.Parameters.AddWithValue("$op", operationId); remove.ExecuteNonQuery();
    }

    // Archived operations are quarantined permanently, including after review. A
    // review creates fresh operations for explicitly selected owners; retrying an
    // old upload must never revive the ambiguous operation or its old binding.
    private bool IsArchivedVideoMetadataOperation(MetadataEdit edit, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        if (!IsVideoMetadataEdit(edit)) return false;
        using var query = Command("SELECT 1 FROM video_metadata_ownership_reviews WHERE operation_id=$op");
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$op", edit.OperationId);
        return query.ExecuteScalar() is not null;
    }

    public IReadOnlyList<VideoOwnershipReview> GetVideoOwnershipReviews()
    {
        using var transaction=BeginReadTransaction();
        using var epochQuery=Command("SELECT epoch FROM sync_space_state WHERE id=1 AND state='active'"); epochQuery.Transaction=transaction;
        var epoch=epochQuery.ExecuteScalar() as string ?? throw new InvalidOperationException("space_fenced");
        var rows=new List<(string Operation,long Content,MetadataEdit Edit)>();
        using (var query=Command("SELECT operation_id,content_id,payload FROM video_metadata_ownership_reviews WHERE state='pending' ORDER BY rowid LIMIT 100"))
        {
            query.Transaction=transaction;
            using var reader=query.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0),reader.GetInt64(1),JsonSerializer.Deserialize<MetadataEdit>(reader.GetString(2))!));
        }
        var result=new List<VideoOwnershipReview>();
        foreach (var row in rows)
        {
            var references=new List<(long Id,string Path)>();
            using (var query=Command("""
                SELECT v.id,v.path FROM videos v WHERE v.id IN (
                    SELECT MIN(video_id) FROM sync_video_owners WHERE content_id=$content AND owner_key NOT LIKE 'ambiguous-source:%' GROUP BY owner_key)
                ORDER BY v.id LIMIT 100
                """))
            {
                query.Transaction=transaction; query.Parameters.AddWithValue("$content",row.Content);
                using var reader=query.ExecuteReader(); while (reader.Read()) references.Add((reader.GetInt64(0),reader.GetString(1)));
            }
            var candidates=new List<VideoOwnershipCandidate>();
            foreach (var reference in references)
            {
                var probe=row.Edit with { EntityId=reference.Id,OperationId=Guid.NewGuid().ToString("N") };
                var current=FindMetadataEdit(probe,transaction);
                candidates.Add(new(reference.Id,reference.Path,current is null?0:Math.Max(current.ServerVersion,current.BaseVersion),current?.OperationId,current?.Value));
            }
            string description;
            if (row.Edit.Kind=="video-tag-link")
            {
                using var tag=Command("SELECT name FROM tags WHERE id=$id"); tag.Transaction=transaction;
                tag.Parameters.AddWithValue("$id",long.Parse(row.Edit.Field,System.Globalization.CultureInfo.InvariantCulture));
                description="标签："+(tag.ExecuteScalar() as string??"已移除的标签");
            }
            else description="分类："+JsonSerializer.Deserialize<string[]>(row.Edit.Field)![0];
            result.Add(new(row.Operation,row.Edit.Kind,row.Edit.Field,row.Edit.Value,candidates,description,epoch));
        }
        result.AddRange(GetVideoFaceOwnershipReviews(transaction,epoch,Math.Max(0,100-result.Count)));
        return result;
    }

    public void ResolveVideoOwnershipReview(VideoOwnershipResolution request,bool authoritative=false)
    {
        if (!Guid.TryParseExact(request.OperationId,"N",out _) || request.Choices is null || request.Choices.Count>100 ||
            request.Discard && request.Choices.Count!=0 || !request.Discard && request.Choices.Count==0 ||
            request.Choices.Any(choice=>choice is null || choice.VideoId<=0 || choice.Version<0 ||
                choice.CurrentOperationId is not null && !Guid.TryParseExact(choice.CurrentOperationId,"N",out _)) ||
            request.Choices.Select(choice=>choice.VideoId).Distinct().Count()!=request.Choices.Count)
            throw new ArgumentException("invalid_video_ownership_resolution");
        var resolution=JsonSerializer.Serialize(request with { Choices=request.Choices.OrderBy(choice=>choice.VideoId).ToArray() });
        using var transaction=BeginTransaction();
        using var epochQuery=Command("SELECT epoch FROM sync_space_state WHERE id=1 AND state='active'"); epochQuery.Transaction=transaction;
        var epoch=epochQuery.ExecuteScalar() as string ?? throw new InvalidOperationException("space_fenced");
        if (request.Epoch is not null && request.Epoch!=epoch || authoritative && request.Epoch is null)
            throw new InvalidOperationException("space_epoch_reset_required");
        if(TryResolveVideoFaceOwnershipReview(request,resolution,transaction))
        { transaction.Commit();return; }
        long content; MetadataEdit proposed;
        using (var query=Command("SELECT content_id,payload,state,resolution_json FROM video_metadata_ownership_reviews WHERE operation_id=$op"))
        {
            query.Transaction=transaction; query.Parameters.AddWithValue("$op",request.OperationId);
            using var reader=query.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("video_ownership_review_missing");
            if (reader.GetString(2)!="pending")
            {
                if (!reader.IsDBNull(3) && reader.GetString(3)==resolution) return;
                throw new InvalidOperationException("video_ownership_review_changed");
            }
            content=reader.GetInt64(0); proposed=JsonSerializer.Deserialize<MetadataEdit>(reader.GetString(1))!;
        }
        var owners=new HashSet<string>(StringComparer.Ordinal);
        foreach (var choice in request.Choices)
        {
            using var owner=Command("SELECT owner_key FROM sync_video_owners WHERE video_id=$id AND content_id=$content AND owner_key NOT LIKE 'ambiguous-source:%'");
            owner.Transaction=transaction; owner.Parameters.AddWithValue("$id",choice.VideoId); owner.Parameters.AddWithValue("$content",content);
            if (owner.ExecuteScalar() is not string key || !owners.Add(key)) throw new ArgumentException("video_ownership_target_invalid");
            var edit=proposed with { EntityId=choice.VideoId,OperationId=Guid.NewGuid().ToString("N"),UpdatedUtc=DateTime.UtcNow.ToString("O"),ServerVersion=0,BaseVersion=choice.Version };
            ValidateMetadataEdit(edit);
            if (!MetadataEntityExists(edit,transaction)) throw new ArgumentException("video_ownership_target_invalid");
            var current=FindMetadataEdit(edit,transaction);
            if ((current is null?0:Math.Max(current.ServerVersion,current.BaseVersion))!=choice.Version || current?.OperationId!=choice.CurrentOperationId)
                throw new InvalidOperationException("video_ownership_review_changed");
            if (authoritative)
            {
                if (current is { ServerVersion:0 })
                {
                    current=AcceptMetadataEditsInTransaction([current],transaction).Single();
                    edit=edit with { BaseVersion=current.ServerVersion };
                }
                var accepted=AcceptMetadataEditsInTransaction([edit],transaction);
                if (accepted.Count!=1 || accepted[0].Value!=edit.Value) throw new InvalidOperationException("video_ownership_review_changed");
            }
            else
            {
                RecordMetadataEdit(edit.Kind,edit.EntityId,edit.Field,edit.Value,transaction);
                var pending=FindMetadataEdit(edit,transaction) ?? throw new InvalidDataException("video_ownership_edit_missing");
                ApplyMetadataEdit(pending,transaction);
            }
        }
        using var complete=Command("UPDATE video_metadata_ownership_reviews SET state=$state,resolution_json=$resolution WHERE operation_id=$op");
        complete.Transaction=transaction; complete.Parameters.AddWithValue("$state",request.Discard?"discarded":"resolved");
        complete.Parameters.AddWithValue("$resolution",resolution); complete.Parameters.AddWithValue("$op",request.OperationId); complete.ExecuteNonQuery();
        transaction.Commit();
    }
}
