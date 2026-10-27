using System.Net.Http.Json;
using Picer.Core.Data;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    internal async Task SynchronizeCatalogRetirementsAsync(SpaceUploadScope scope,CancellationToken cancellationToken=default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease=await AcquirePublisherLeaseAsync(cancellationToken).ConfigureAwait(false);
            using var database=localDatabase.OpenIndependentConnection();
            var session=accountClient.Connection.Session ?? throw new InvalidOperationException("device_login_required");
            SpacePublisher publisher;
            using(var query=database.Command("""
                SELECT publisher_id,library_id,incarnation_id,epoch,last_sequence,metadata_protocol FROM sync_publishers
                WHERE device_grant_id=$grant AND space_id=$space AND incarnation_id=$device AND state='publishing'
                """))
            {
                query.Parameters.AddWithValue("$grant",session.GrantId);query.Parameters.AddWithValue("$space",session.SpaceId);
                query.Parameters.AddWithValue("$device",session.DeviceId);
                using var row=query.ExecuteReader();
                if(!row.Read()) throw new InvalidOperationException("initial_publication_required");
                publisher=new(row.GetString(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetInt64(4),row.GetInt32(5));
            }
            SpaceSyncProtocol.RequireMetadataSupport(publisher);
            await ResumeCatalogCompletionsAsync(database,publisher,scope,cancellationToken).ConfigureAwait(false);
            await PushCatalogRetirementsAsync(database,publisher,scope,null,cancellationToken).ConfigureAwait(false);
            await PullCatalogRetirementsAsync(database,publisher,scope,null,cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // Each network phase has its own durable receipt; normal operation-triggered
    // synchronization invokes these after the publication cut and metadata pull.
    private async Task PushCatalogRetirementsAsync(PicerDatabase database,SpacePublisher publisher,SpaceUploadScope scope,
        IProgress<string>? progress,CancellationToken cancellationToken)
    {
        if(!CatalogGenerationBaselineReady(database,publisher.PublisherId)) return;
        CatalogRetirementCursor? cursor=null;
        var applied=0;var review=0;
        while(true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var anchors=database.GetPendingCatalogRetirementAnchors(publisher.PublisherId,scope,cursor,200,requirePublishedCut:true);
            if(anchors.Count==0) break;
            var response=await CatalogPostAsync<SpaceCatalogRetirementRequest,SpaceCatalogRetirementResponse>(
                "api/sync/v2/catalog-retirements/apply",new(publisher.PublisherId,publisher.Epoch,scope,anchors),cancellationToken).ConfigureAwait(false);
            ValidateCatalogResponseIdentity(publisher,response.PublisherId,response.Epoch);
            if(response.Results is null || response.Results.Count!=anchors.Count || response.Results.Any(item=>item is null || item.Result is null))
                throw new InvalidDataException("catalog_retirement_receipt_invalid");
            var results=response.Results.ToDictionary(item=>new CatalogRetirementCursor(item.OperationId,item.MediaKind,item.MediaGlobalId),item=>item.Result);
            if(!results.Keys.ToHashSet().SetEquals(anchors.Select(anchor=>anchor.Cursor)))
                throw new InvalidDataException("catalog_retirement_receipt_invalid");
            using(var transaction=database.BeginTransaction())
            {
                ValidateLocalCatalogPublisher(database,publisher);
                foreach(var anchor in anchors)
                {
                    var result=results[anchor.Cursor];
                    database.RecordCatalogRetirementDelivery(publisher.PublisherId,anchor,result,scope,transaction);
                    if(result.State is "applied" or "kept") applied++;else review++;
                }
                transaction.Commit();
            }
            cursor=anchors[^1].Cursor;
        }
        var after="";
        while(true)
        {
            var manifests=database.GetReadyCatalogRetirements(publisher.PublisherId,scope,after);
            if(manifests.Count==0) break;
            var results=await CompleteRemoteCatalogsAsync(publisher,scope,manifests,cancellationToken).ConfigureAwait(false);
            using(var transaction=database.BeginTransaction())
            {
                ValidateLocalCatalogPublisher(database,publisher);
                foreach(var manifest in manifests.Where(item=>results[item.OperationId].State=="completed"))
                {
                    database.StageCatalogCompletions(publisher.PublisherId,publisher.Epoch,[manifest],transaction);
                    var local=database.FinalizeCatalogRetirement(publisher.PublisherId,manifest.OperationId,manifest.Kind,manifest.GlobalId,manifest.MediaCount,scope,transaction);
                    database.RecordCatalogCompletionInboxResult(publisher.PublisherId,publisher.Epoch,manifest.OperationId,local,true,transaction);
                }
                transaction.Commit();
            }
            after=manifests[^1].OperationId;
        }
        if(applied>0 || review>0) progress?.Report($"分类删除已上传：{applied} 项关联已确认，{review} 项待处理。");
        await CompactCatalogRetirementsAsync(database,publisher,scope,cancellationToken).ConfigureAwait(false);
    }

    private async Task PullCatalogRetirementsAsync(PicerDatabase database,SpacePublisher publisher,SpaceUploadScope scope,
        IProgress<string>? progress,CancellationToken cancellationToken,bool reviewedOnly=false)
    {
        if(!CatalogGenerationBaselineReady(database,publisher.PublisherId)) return;
        await ResumeCatalogCompletionsAsync(database,publisher,scope,cancellationToken).ConfigureAwait(false);
        CatalogRetirementCursor? cursor=null;
        var applied=0;var review=0;
        while(true)
        {
            var page=await CatalogPostAsync<SpaceCatalogRetirementReadRequest,SpaceCatalogRetirementPage>(
                "api/sync/v2/catalog-retirements/read",new(publisher.PublisherId,publisher.Epoch,scope,cursor,200),cancellationToken).ConfigureAwait(false);
            ValidateCatalogResponseIdentity(publisher,page.PublisherId,page.Epoch);
            if(page.Anchors is null || page.Anchors.Count>200 || page.HasMore && page.Anchors.Count==0)
                throw new InvalidDataException("catalog_retirement_page_invalid");
            var previous=cursor;
            foreach(var anchor in page.Anchors)
            {
                if(anchor is null || previous is not null && CompareCatalogCursors(anchor.Cursor,previous)<=0)
                    throw new InvalidDataException("catalog_retirement_cursor_invalid");
                previous=anchor.Cursor;
            }
            if(page.Next!=previous) throw new InvalidDataException("catalog_retirement_cursor_invalid");
            var selected=reviewedOnly?page.Anchors.Where(anchor=>HasReviewedCatalogReceipt(database,publisher.PublisherId,anchor)).ToArray():page.Anchors;
            if(selected.Count>0)
            {
                var receipts=new List<SpaceCatalogRetirementDelivery>();
                using(var transaction=database.BeginTransaction())
                {
                    ValidateLocalCatalogPublisher(database,publisher);
                    using(var suppress=database.Command("INSERT INTO sync_suppression(id) VALUES(1)")) suppress.ExecuteNonQuery();
                    foreach(var anchor in selected)
                    {
                        var result=database.ApplyCatalogRetirementAssociation(publisher.PublisherId,anchor,scope,transaction);
                        receipts.Add(new(anchor,result));if(result.State is "applied" or "kept") applied++;else review++;
                    }
                    using(var release=database.Command("DELETE FROM sync_suppression WHERE id=1")) release.ExecuteNonQuery();
                    transaction.Commit();
                }
                var receipt=await CatalogPostAsync<SpaceCatalogRetirementDeliveryRequest,SpaceCatalogRetirementDeliveryResponse>(
                    "api/sync/v2/catalog-retirements/acknowledge",new(publisher.PublisherId,publisher.Epoch,scope,receipts),cancellationToken).ConfigureAwait(false);
                ValidateCatalogResponseIdentity(publisher,receipt.PublisherId,receipt.Epoch);
                if(receipt.Accepted is null || receipt.Accepted.Count!=receipts.Count
                    || !receipt.Accepted.ToHashSet().SetEquals(receipts.Select(item=>item.Anchor.Cursor)))
                    throw new InvalidDataException("catalog_retirement_acknowledgement_invalid");
            }
            cursor=page.Next;if(!page.HasMore) break;
        }
        var after="";
        while(true)
        {
            var page=await CatalogPostAsync<SpaceCatalogCompletionReadRequest,SpaceCatalogCompletionPage>(
                "api/sync/v2/catalog-retirements/completions",new(publisher.PublisherId,publisher.Epoch,scope,after),cancellationToken).ConfigureAwait(false);
            ValidateCatalogResponseIdentity(publisher,page.PublisherId,page.Epoch);
            if(page.Items is null || page.Items.Count>100 || page.HasMore && page.Items.Count==0)
                throw new InvalidDataException("catalog_completion_page_invalid");
            var next=after;
            foreach(var item in page.Items)
            {
                if(item is null || string.CompareOrdinal(item.OperationId,next)<=0) throw new InvalidDataException("catalog_completion_cursor_invalid");
                next=item.OperationId;
            }
            if(page.Next!=next) throw new InvalidDataException("catalog_completion_cursor_invalid");
            using(var transaction=database.BeginTransaction())
            {
                ValidateLocalCatalogPublisher(database,publisher);
                database.StageCatalogCompletions(publisher.PublisherId,publisher.Epoch,page.Items,transaction);
                transaction.Commit();
            }
            after=page.Next;if(!page.HasMore) break;
        }
        await ResumeCatalogCompletionsAsync(database,publisher,scope,cancellationToken).ConfigureAwait(false);
        if(applied>0 || review>0) progress?.Report($"分类删除已接收：{applied} 项关联已应用，{review} 项待处理。");
        await CompactCatalogRetirementsAsync(database,publisher,scope,cancellationToken).ConfigureAwait(false);
    }

    private static bool HasReviewedCatalogReceipt(PicerDatabase database,string publisher,CatalogRetirementAnchor? anchor=null)
    {
        using var query=database.Command("""
            SELECT 1 FROM sync_catalog_retirement_receipts r WHERE r.publisher_id=$publisher AND r.state IN ('applied','kept')
                AND ($operation IS NULL OR (r.operation_id=$operation AND r.media_kind=$kind AND r.media_global_id=$media))
                AND NOT EXISTS(SELECT 1 FROM sync_retired_catalog_identities retired
                    WHERE retired.publisher_id=r.publisher_id AND retired.kind=r.kind AND retired.global_id=r.global_id) LIMIT 1
            """);
        query.Parameters.AddWithValue("$publisher",publisher);query.Parameters.AddWithValue("$operation",(object?)anchor?.OperationId??DBNull.Value);
        query.Parameters.AddWithValue("$kind",(object?)anchor?.MediaKind??DBNull.Value);query.Parameters.AddWithValue("$media",(object?)anchor?.MediaGlobalId??DBNull.Value);
        return query.ExecuteScalar() is not null;
    }

    private async Task ResumeCatalogCompletionsAsync(PicerDatabase database,SpacePublisher publisher,SpaceUploadScope scope,CancellationToken cancellationToken)
    {
        var after="";
        while(true)
        {
            var items=database.GetPendingCatalogCompletions(publisher.PublisherId,publisher.Epoch,after);
            if(items.Count==0) return;
            // The manifest is durable before HTTP. Confirm the authority first:
            // a remote edit racing completion must not leave a deleted local object
            // without a way to receive that edit. A lost reply retries the same manifest.
            var remote=await CompleteRemoteCatalogsAsync(publisher,scope,items,cancellationToken).ConfigureAwait(false);
            using(var transaction=database.BeginTransaction())
            {
                ValidateLocalCatalogPublisher(database,publisher);
                foreach(var item in items)
                {
                    var confirmed=remote[item.OperationId];
                    var result=confirmed.State=="completed"
                        ?database.FinalizeCatalogRetirement(publisher.PublisherId,item.OperationId,item.Kind,item.GlobalId,item.MediaCount,scope,transaction)
                        :confirmed;
                    database.RecordCatalogCompletionInboxResult(publisher.PublisherId,publisher.Epoch,item.OperationId,result,confirmed.State=="completed",transaction);
                }
                transaction.Commit();
            }
            after=items[^1].OperationId;
        }
    }

    private async Task<Dictionary<string,CatalogRetirementCompletion>> CompleteRemoteCatalogsAsync(SpacePublisher publisher,
        SpaceUploadScope scope,IReadOnlyList<CatalogRetirementManifest> items,CancellationToken cancellationToken,bool compact=false)
    {
        var response=await CatalogPostAsync<SpaceCatalogCompletionRequest,SpaceCatalogCompletionResponse>(
            compact?"api/sync/v2/catalog-retirements/compact":"api/sync/v2/catalog-retirements/complete",new(publisher.PublisherId,publisher.Epoch,scope,items),cancellationToken).ConfigureAwait(false);
        ValidateCatalogResponseIdentity(publisher,response.PublisherId,response.Epoch);
        if(response.Results is null || response.Results.Count!=items.Count || response.Results.Any(item=>item is null || item.Result is null
            || item.Result.State is not ("completed" or "pending" or "conflict" or "scope_conflict")))
            throw new InvalidDataException("catalog_completion_receipt_invalid");
        var results=response.Results.ToDictionary(item=>item.OperationId,item=>item.Result,StringComparer.Ordinal);
        if(!results.Keys.ToHashSet().SetEquals(items.Select(item=>item.OperationId))) throw new InvalidDataException("catalog_completion_receipt_invalid");
        return results;
    }

    private async Task CompactCatalogRetirementsAsync(PicerDatabase database,SpacePublisher publisher,SpaceUploadScope scope,CancellationToken cancellationToken)
    {
        var after="";
        while(true)
        {
            var items=database.GetCatalogCompactionCandidates(publisher.PublisherId,scope,after);
            if(items.Count==0) return;
            var confirmed=await CompleteRemoteCatalogsAsync(publisher,scope,items,cancellationToken,compact:true).ConfigureAwait(false);
            using(var transaction=database.BeginTransaction())
            {
                ValidateLocalCatalogPublisher(database,publisher);
                foreach(var item in items.Where(item=>confirmed[item.OperationId].State=="completed"))
                    database.CompactCatalogRetirement(publisher.PublisherId,item,scope,transaction);
                transaction.Commit();
            }
            after=items[^1].OperationId;
        }
    }

    private async Task<TResponse> CatalogPostAsync<TRequest,TResponse>(string path,TRequest request,CancellationToken cancellationToken)
    {
        using var response=await accountClient.SpaceClient.PostAsJsonAsync(path,request,DeviceAuthorization.Json,cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(DeviceAuthorization.Json,cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("catalog_sync_response_missing");
    }

    private static bool CatalogGenerationBaselineReady(PicerDatabase database,string publisher)
    {
        using var query=database.Command("""
            SELECT 1 FROM sync_publishers p WHERE p.publisher_id=$publisher AND p.state='publishing' AND p.metadata_protocol>=10
                AND NOT EXISTS(SELECT 1 FROM sync_exports e WHERE e.publisher_id=p.publisher_id AND e.metadata_protocol<10)
            """);
        query.Parameters.AddWithValue("$publisher",publisher);return query.ExecuteScalar() is not null;
    }

    private static void ValidateLocalCatalogPublisher(PicerDatabase database,SpacePublisher publisher)
    {
        using var query=database.Command("SELECT 1 FROM sync_publishers WHERE publisher_id=$publisher AND epoch=$epoch AND state='publishing'");
        query.Parameters.AddWithValue("$publisher",publisher.PublisherId);query.Parameters.AddWithValue("$epoch",publisher.Epoch);
        if(query.ExecuteScalar() is null) throw new InvalidDataException("catalog_publisher_epoch_changed");
    }

    private static void ValidateCatalogResponseIdentity(SpacePublisher publisher,string publisherId,string epoch)
    {
        if(publisherId!=publisher.PublisherId || epoch!=publisher.Epoch) throw new InvalidDataException("catalog_publisher_epoch_mismatch");
    }

    private static int CompareCatalogCursors(CatalogRetirementCursor first,CatalogRetirementCursor second)
    {
        var order=string.CompareOrdinal(first.OperationId,second.OperationId);
        if(order==0) order=string.CompareOrdinal(first.MediaKind,second.MediaKind);
        return order==0?string.CompareOrdinal(first.MediaGlobalId,second.MediaGlobalId):order;
    }
}
