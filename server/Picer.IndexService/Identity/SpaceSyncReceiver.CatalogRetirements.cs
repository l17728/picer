using System.Text.Json;
using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncReceiver
{
    public SpaceCatalogCompletionPage ReadCatalogCompletions(SpaceCatalogCompletionReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(request.PageSize is <1 or >100) throw new ArgumentException("invalid_catalog_completion_page");
        using var transaction=database.BeginReadTransaction();
        ValidateAdoptionPublisher(request.PublisherId,request.Epoch,request.Scope);
        var scope=ResolveCatalogRetirementScope(request.PublisherId,request.Scope);
        var rows=database.GetReadyCatalogRetirements(request.PublisherId,scope,request.After,request.PageSize+1);
        var items=rows.Take(request.PageSize).ToArray();
        return new(request.PublisherId,request.Epoch,items,items.Length>0?items[^1].OperationId:request.After,rows.Count>request.PageSize);
    }

    public SpaceCatalogCompletionResponse CompleteCatalogRetirements(SpaceCatalogCompletionRequest request,bool compact=false)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(request.Items is null || request.Items.Count is <1 or >100 || request.Items.Any(item=>item is null)
            || request.Items.Select(item=>item.OperationId).Distinct().Count()!=request.Items.Count)
            throw new ArgumentException("invalid_catalog_completion_batch");
        using var transaction=database.BeginTransaction();
        ValidateAdoptionPublisher(request.PublisherId,request.Epoch,request.Scope);
        var scope=ResolveCatalogRetirementScope(request.PublisherId,request.Scope);
        var before=Convert.ToInt64(Scalar("SELECT total_changes()"));
        var results=request.Items.Select(item=>new SpaceCatalogCompletionItemResult(item.OperationId,
            compact?database.CompactCatalogRetirement(request.PublisherId,item,scope,transaction)
                :database.FinalizeCatalogRetirement(request.PublisherId,item.OperationId,item.Kind,item.GlobalId,item.MediaCount,scope,transaction))).ToArray();
        if(Convert.ToInt64(Scalar("SELECT total_changes()"))!=before) SignalCatalogRetirementChange(request.PublisherId);
        transaction.Commit();return new(request.PublisherId,request.Epoch,results);
    }

    // Association execution and catalogue finalization are separate durable phases.
    public SpaceCatalogRetirementDeliveryResponse AcknowledgeCatalogRetirements(SpaceCatalogRetirementDeliveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(request.Items is null || request.Items.Count is <1 or >200
            || request.Items.Any(item=>item is null || item.Anchor is null || item.Result is null)
            || request.Items.Select(item=>item.Anchor.Cursor).Distinct().Count()!=request.Items.Count)
            throw new ArgumentException("invalid_catalog_retirement_receipts");
        using var transaction=database.BeginTransaction();
        ValidateAdoptionPublisher(request.PublisherId,request.Epoch,request.Scope);
        var scope=ResolveCatalogRetirementScope(request.PublisherId,request.Scope);
        var changed=false;
        foreach(var item in request.Items)
            changed|=database.RecordCatalogRetirementDelivery(request.PublisherId,item.Anchor,item.Result,scope,transaction);
        if(changed) SignalCatalogRetirementChange(request.PublisherId);
        transaction.Commit();return new(request.PublisherId,request.Epoch,request.Items.Select(item=>item.Anchor.Cursor).ToArray());
    }

    public SpaceCatalogRetirementResponse ApplyCatalogRetirements(SpaceCatalogRetirementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(request.Anchors is null || request.Anchors.Count is <1 or >200 || request.Anchors.Any(a=>a is null)
            || request.Anchors.Select(a=>(a.OperationId,a.MediaKind,a.MediaGlobalId)).Distinct().Count()!=request.Anchors.Count)
            throw new ArgumentException("invalid_catalog_retirement_batch");
        using var transaction=database.BeginTransaction();
        ValidateAdoptionPublisher(request.PublisherId,request.Epoch,request.Scope);
        var scope=ResolveCatalogRetirementScope(request.PublisherId,request.Scope);
        var ownsSuppression=Scalar("SELECT 1 FROM sync_suppression WHERE id=1") is null;
        if(ownsSuppression) Execute("INSERT INTO sync_suppression(id) VALUES(1)");
        var before=Convert.ToInt64(Scalar("SELECT total_changes()"));
        var results=new List<SpaceCatalogRetirementItemResult>();
        foreach(var anchor in request.Anchors)
            results.Add(new(anchor.OperationId,anchor.MediaKind,anchor.MediaGlobalId,
                database.ApplyCatalogRetirementAssociation(request.PublisherId,anchor,scope,transaction)));
        var changed=Convert.ToInt64(Scalar("SELECT total_changes()"))!=before;
        if(ownsSuppression) Execute("DELETE FROM sync_suppression WHERE id=1");
        if(changed) SignalCatalogRetirementChange(request.PublisherId);
        transaction.Commit();return new(request.PublisherId,request.Epoch,results);
    }

    public SpaceCatalogRetirementPage ReadCatalogRetirements(SpaceCatalogRetirementReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(request.PageSize is <1 or >200 || request.After is { } after
            && (after.OperationId is null || after.MediaGlobalId is null || after.MediaKind is null
                || after.OperationId.Length>32 || after.MediaGlobalId.Length>32 || after.MediaKind.Length>8))
            throw new ArgumentException("invalid_catalog_retirement_cursor");
        using var transaction=database.BeginReadTransaction();
        ValidateAdoptionPublisher(request.PublisherId,request.Epoch,request.Scope);
        var scope=ResolveCatalogRetirementScope(request.PublisherId,request.Scope);
        var rows=database.GetPendingCatalogRetirementAnchors(request.PublisherId,scope,request.After,request.PageSize+1);
        var items=rows.Take(request.PageSize).ToArray();
        return new(request.PublisherId,request.Epoch,items,items.Length>0?items[^1].Cursor:request.After,rows.Count>request.PageSize);
    }

    private SpaceUploadScope ResolveCatalogRetirementScope(string publisher,SpaceUploadScope scope)
    {
        using var query=Sql("SELECT source_id FROM sync_source_bindings WHERE publisher_id=$publisher AND local_source_id IN(SELECT value FROM json_each($sources))",
            ("$publisher",publisher),("$sources",JsonSerializer.Serialize(scope.SourceIds)));
        using var rows=query.ExecuteReader();var sources=new List<long>();while(rows.Read()) sources.Add(rows.GetInt64(0));
        return scope with { SourceIds=sources };
    }

    private void SignalCatalogRetirementChange(string publisher)
    {
        Execute("UPDATE mirror_revision SET metadata_revision=metadata_revision+1 WHERE id=1; UPDATE sync_space_state SET change_sequence=change_sequence+1 WHERE id=1;");
        Execute("""
            INSERT INTO sync_publisher_notifications(publisher_id,revision) VALUES($publisher,1)
            ON CONFLICT(publisher_id) DO UPDATE SET revision=revision+1
            """,("$publisher",publisher));
    }
}
