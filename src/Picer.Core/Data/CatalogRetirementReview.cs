using Microsoft.Data.Sqlite;
using Picer.Core.Identity;

namespace Picer.Core.Data;

public sealed record CatalogRetirementReview(string PublisherId,string Epoch,CatalogRetirementAnchor Anchor,
    string Title,string MediaPath,string State,string CurrentFingerprint,long MediaId=0,bool CompletionOnly=false);
public sealed record CatalogRetirementDecision(CatalogRetirementReview Review,bool KeepCurrent);

public sealed partial class PicerDatabase
{
    public long CountPendingCatalogSynchronization(string? publisher=null)
    {
        using var query=Command("""
            SELECT COUNT(*) FROM (
                SELECT publisher_id,operation_id FROM sync_catalog_retirements
                    WHERE state='pending' AND ($publisher IS NULL OR publisher_id=$publisher)
                UNION SELECT r.publisher_id,r.operation_id FROM sync_catalog_retirement_receipts r
                    WHERE ($publisher IS NULL OR r.publisher_id=$publisher)
                        AND NOT EXISTS(SELECT 1 FROM sync_retired_catalog_identities retired
                            WHERE retired.publisher_id=r.publisher_id AND retired.kind=r.kind AND retired.global_id=r.global_id)
                UNION SELECT publisher_id,operation_id FROM sync_catalog_completion_inbox
                    WHERE $publisher IS NULL OR publisher_id=$publisher
                UNION SELECT r.publisher_id,r.operation_id FROM sync_catalog_completion_reviews r
                    WHERE ($publisher IS NULL OR r.publisher_id=$publisher)
                        AND NOT EXISTS(SELECT 1 FROM sync_retired_catalog_identities retired
                            WHERE retired.publisher_id=r.publisher_id AND retired.kind=r.kind AND retired.global_id=r.global_id))
            """);
        query.Parameters.AddWithValue("$publisher",(object?)publisher??DBNull.Value);return Convert.ToInt64(query.ExecuteScalar());
    }

    public IReadOnlyList<CatalogRetirementReview> GetCatalogRetirementReviews(int limit=200)
    {
        if(limit is <1 or >500) throw new ArgumentOutOfRangeException(nameof(limit));
        using var ownedTransaction = _readSnapshotTransaction is null ? BeginReadTransaction() : null;
        var transaction = _readSnapshotTransaction ?? ownedTransaction!;
        var candidates=new List<(string Publisher,string Epoch,CatalogRetirementAnchor Anchor,string State,long Media,long Catalog,bool Completion)>();
        using(var query=Command("""
            WITH review AS (
                SELECT r.publisher_id,r.operation_id,r.kind,r.global_id,r.media_kind,r.media_global_id,r.state,r.media_id,r.catalog_id,r.created_utc,0 completion
                FROM sync_catalog_retirement_receipts r WHERE r.state IN ('conflict','baseline_required','scope_conflict')
                    AND NOT EXISTS(SELECT 1 FROM sync_catalog_retirements origin WHERE origin.publisher_id=r.publisher_id AND origin.operation_id=r.operation_id)
                UNION ALL SELECT publisher_id,operation_id,kind,global_id,media_kind,media_global_id,state,media_id,catalog_id,created_utc,1
                    FROM sync_catalog_completion_reviews WHERE state='pending')
            SELECT r.publisher_id,p.epoch,r.operation_id,r.kind,r.global_id,r.media_kind,r.media_global_id,r.state,r.media_id,r.catalog_id,
                CASE r.media_kind WHEN 'image' THEN i.source_id ELSE v.source_id END,r.completion
            FROM review r JOIN sync_publishers p ON p.publisher_id=r.publisher_id
            LEFT JOIN images i ON r.media_kind='image' AND i.id=r.media_id
            LEFT JOIN videos v ON r.media_kind='video' AND v.id=r.media_id
            WHERE p.state IN ('publishing','receiving')
                AND (i.id IS NOT NULL OR v.id IS NOT NULL)
                AND NOT EXISTS(SELECT 1 FROM sync_retired_catalog_identities retired
                    WHERE retired.publisher_id=r.publisher_id AND retired.kind=r.kind AND retired.global_id=r.global_id)
            ORDER BY r.created_utc,r.publisher_id,r.operation_id,r.media_kind,r.media_global_id LIMIT $limit
            """))
        {
            query.Transaction=transaction;query.Parameters.AddWithValue("$limit",limit);
            using var rows=query.ExecuteReader();
            while(rows.Read()) candidates.Add((rows.GetString(0),rows.GetString(1),
                new(rows.GetString(2),rows.GetString(3),rows.GetString(4),rows.GetString(5),rows.GetString(6),rows.GetInt64(10)),
                rows.GetString(7),rows.GetInt64(8),rows.GetInt64(9),rows.GetBoolean(11)));
        }
        var result=new List<CatalogRetirementReview>();
        foreach(var candidate in candidates)
        {
            var table=candidate.Anchor.Kind switch { "tag"=>"tags","album"=>"albums",_=>"people" };
            var column=candidate.Anchor.Kind=="person"?"display_name":"name";
            using var title=Command($"SELECT {column} FROM {table} WHERE id=$id");
            title.Transaction=transaction;title.Parameters.AddWithValue("$id",candidate.Catalog);
            var label=candidate.Anchor.Kind switch { "tag"=>"标签","album"=>"相册",_=>"人物" };
            using var media=Command($"SELECT path FROM {(candidate.Anchor.MediaKind=="image"?"images":"videos")} WHERE id=$id");
            media.Transaction=transaction;media.Parameters.AddWithValue("$id",candidate.Media);
            result.Add(new(candidate.Publisher,candidate.Epoch,candidate.Anchor,
                title.ExecuteScalar() is string name?$"{label}：{name}":$"已删除的{label}",media.ExecuteScalar() as string??"",
                candidate.State,GetCatalogRetirementFingerprint(candidate.Anchor.Kind,candidate.Catalog,candidate.Anchor.MediaKind,candidate.Media,transaction,candidate.Publisher),candidate.Media,candidate.Completion));
        }
        return result;
    }

    public void ResolveCatalogRetirementReview(CatalogRetirementDecision decision,SpaceUploadScope scope)
    {
        ArgumentNullException.ThrowIfNull(decision);ArgumentNullException.ThrowIfNull(decision.Review);
        var review=decision.Review;var anchor=review.Anchor;
        using var transaction=BeginTransaction();
        using(var publisher=Command("SELECT 1 FROM sync_publishers WHERE publisher_id=$publisher AND epoch=$epoch AND state IN ('publishing','receiving')"))
        {
            publisher.Transaction=transaction;publisher.Parameters.AddWithValue("$publisher",review.PublisherId);
            publisher.Parameters.AddWithValue("$epoch",review.Epoch);
            if(publisher.ExecuteScalar() is null) throw new InvalidDataException("catalog_review_epoch_changed");
        }
        var (media,catalog,direction)=ResolveCatalogRetirementIdentity(review.PublisherId,anchor,scope,transaction);
        var reviewTable=review.CompletionOnly?"sync_catalog_completion_reviews":"sync_catalog_retirement_receipts";
        using(var pending=Command($"""
            SELECT state FROM {reviewTable} WHERE publisher_id=$publisher AND operation_id=$operation
                AND kind=$kind AND global_id=$global AND media_kind=$mediaKind AND media_global_id=$media
                AND ($completion=1 OR NOT EXISTS(SELECT 1 FROM sync_catalog_retirements origin WHERE origin.publisher_id=$publisher AND origin.operation_id=$operation))
                AND NOT EXISTS(SELECT 1 FROM sync_retired_catalog_identities retired
                    WHERE retired.publisher_id=$publisher AND retired.kind=$kind AND retired.global_id=$global)
            """))
        {
            pending.Transaction=transaction;pending.Parameters.AddWithValue("$publisher",review.PublisherId);
            pending.Parameters.AddWithValue("$operation",anchor.OperationId);pending.Parameters.AddWithValue("$kind",anchor.Kind);
            pending.Parameters.AddWithValue("$global",anchor.GlobalId);pending.Parameters.AddWithValue("$mediaKind",anchor.MediaKind);
            pending.Parameters.AddWithValue("$media",anchor.MediaGlobalId);
            pending.Parameters.AddWithValue("$completion",review.CompletionOnly?1:0);
            if(pending.ExecuteScalar() is not string state || (review.CompletionOnly?state!="pending":state is not ("conflict" or "baseline_required" or "scope_conflict")))
                throw new InvalidDataException("catalog_review_no_longer_pending");
        }
        var fingerprint=GetCatalogRetirementFingerprint(anchor.Kind,catalog,anchor.MediaKind,media,transaction,review.PublisherId);
        if(fingerprint!=review.CurrentFingerprint) throw new InvalidDataException("catalog_review_content_changed");
        if(!decision.KeepCurrent && anchor.MediaKind=="video" && !CanRetireVideoContentRelationships(review.PublisherId,media,scope,transaction))
            throw new UnauthorizedAccessException("catalog_retirement_outside_scope");
        IReadOnlyList<MetadataEdit> edits=[];
        if(!decision.KeepCurrent)
        {
            edits=RemoveCatalogRetirementRelationships(anchor.Kind,catalog,anchor.MediaKind,media,transaction);
            RegisterCatalogRetirementEdits(review.PublisherId,direction,edits,transaction);
        }
        using(var save=Command($"""
            UPDATE {reviewTable} SET state=$state,current_fingerprint=$fingerprint,applied_operations=$edits
            WHERE publisher_id=$publisher AND operation_id=$operation AND media_kind=$mediaKind AND media_global_id=$media;
            UPDATE mirror_revision SET metadata_revision=metadata_revision+1 WHERE id=1;
            """))
        {
            save.Transaction=transaction;save.Parameters.AddWithValue("$state",decision.KeepCurrent?"kept":"applied");
            save.Parameters.AddWithValue("$fingerprint",fingerprint);save.Parameters.AddWithValue("$edits",System.Text.Json.JsonSerializer.Serialize(edits.Select(edit=>edit.OperationId)));
            save.Parameters.AddWithValue("$publisher",review.PublisherId);save.Parameters.AddWithValue("$operation",anchor.OperationId);
            save.Parameters.AddWithValue("$mediaKind",anchor.MediaKind);save.Parameters.AddWithValue("$media",anchor.MediaGlobalId);save.ExecuteNonQuery();
        }
        if(direction=="receiving")
        {
            using var notify=Command("""
                UPDATE sync_space_state SET change_sequence=change_sequence+1 WHERE id=1;
                INSERT INTO sync_publisher_notifications(publisher_id,revision) VALUES($publisher,1)
                    ON CONFLICT(publisher_id) DO UPDATE SET revision=revision+1;
                """);
            notify.Transaction=transaction;notify.Parameters.AddWithValue("$publisher",review.PublisherId);notify.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private void RegisterCatalogRetirementEdits(string publisher,string direction,IReadOnlyList<MetadataEdit> edits,SqliteTransaction transaction)
    {
        if(direction=="receiving") { PromoteUnversionedMetadata(transaction);return; }
        using var imported=Command("""
            INSERT INTO sync_imported_metadata_edits(publisher_id,operation_id,entity_kind,entity_id,entity_field)
            VALUES($publisher,$operation,$kind,$entity,$field)
            """);
        imported.Transaction=transaction;
        foreach(var edit in edits)
        {
            imported.Parameters.Clear();imported.Parameters.AddWithValue("$publisher",publisher);
            imported.Parameters.AddWithValue("$operation",edit.OperationId);imported.Parameters.AddWithValue("$kind",edit.Kind);
            imported.Parameters.AddWithValue("$entity",edit.EntityId);imported.Parameters.AddWithValue("$field",edit.Field);imported.ExecuteNonQuery();
        }
    }
}
