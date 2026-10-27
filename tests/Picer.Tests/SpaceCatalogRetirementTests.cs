using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.IndexService.Identity;

namespace Picer.Tests;

public sealed class SpaceCatalogRetirementTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"picer-space-retirement-"+Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    public SpaceCatalogRetirementTests() => _database=new(Path.Combine(_root,"picer.db"));

    [Fact]
    public void RemoteReattachmentOffersANewIdentityForARetiredSharedTag()
    {
        var a=Seed();var b=Seed();
        var anchor=new CatalogRetirementAnchor(Guid.NewGuid().ToString("N"),"tag",a.CatalogGlobal,"image",a.MediaGlobal,77)
            { Fingerprint=_database.GetCatalogRetirementFingerprint("tag",a.Tag,"image",a.Image) };
        a.Receiver.ApplyCatalogRetirements(new(a.Publisher.PublisherId,a.Publisher.Epoch,new([77]),[anchor]));
        using(var transaction=_database.BeginTransaction())
        {
            Assert.Equal("completed",_database.FinalizeCatalogRetirement(a.Publisher.PublisherId,anchor.OperationId,"tag",a.CatalogGlobal,1,
                new([_database.GetImage(a.Image)!.SourceId]),transaction).State);
            transaction.Commit();
        }
        var request=new SpaceIdentityOfferRequest(a.Publisher.PublisherId,a.Publisher.Epoch,new([77]));
        Assert.Empty(a.Receiver.ReadIdentityOffers(request).Offers);
        SemanticEngine.AssignTag(_database,a.Image,a.Tag);
        var offer=Assert.Single(a.Receiver.ReadIdentityOffers(request).Offers);
        Assert.Equal("tag",offer.Kind);Assert.Equal(a.MediaGlobal,offer.MediaGlobalId);
        var replacement=Guid.NewGuid().ToString("N");
        var adoption=new SpaceIdentityAdoption(offer.OfferId,"tag",800,replacement);
        Assert.Equal(adoption,Assert.Single(a.Receiver.AdoptIdentities(new(a.Publisher.PublisherId,a.Publisher.Epoch,new([77]),[adoption])).Items));
        Assert.Equal(a.Tag,Target(a.Publisher.PublisherId,"tag",replacement));
        Assert.True(_database.IsRetiredCatalogIdentity(a.Publisher.PublisherId,"tag",a.CatalogGlobal));
        Assert.Single(SemanticEngine.GetImageTags(_database,b.Image));
        Assert.Contains(a.Receiver.ReadMetadataEdits().Edits,edit=>edit.Kind=="tag-link" && edit.EntityId==700 && edit.Field=="800" && edit.Value=="1");
    }

    [Fact]
    public void EmptyCatalogHasACompletionManifestAndDoesNotCompleteAnotherSource()
    {
        var a=Seed();var b=Seed();
        SemanticEngine.RemoveTag(_database,a.Image,a.Tag);
        SemanticEngine.DeleteTag(_database,a.Tag);
        var read=new SpaceCatalogCompletionReadRequest(a.Publisher.PublisherId,a.Publisher.Epoch,new([77]),PageSize:1);
        var page=a.Receiver.ReadCatalogCompletions(read);
        var manifest=Assert.Single(page.Items);Assert.Equal(0,manifest.MediaCount);Assert.False(page.HasMore);
        Assert.Empty(b.Receiver.ReadCatalogCompletions(new(b.Publisher.PublisherId,b.Publisher.Epoch,new([77]))).Items);
        Assert.Empty(a.Receiver.ReadCatalogCompletions(read with { After=page.Next }).Items);
        Assert.Empty(a.Receiver.ReadCatalogCompletions(read with { Scope=new([999]) }).Items);
        Assert.Throws<UnauthorizedAccessException>(()=>b.Receiver.ReadCatalogCompletions(read));
        var request=new SpaceCatalogCompletionRequest(a.Publisher.PublisherId,a.Publisher.Epoch,new([77]),[manifest]);
        Assert.Throws<UnauthorizedAccessException>(()=>b.Receiver.CompleteCatalogRetirements(request));
        Assert.Throws<InvalidOperationException>(()=>a.Receiver.CompleteCatalogRetirements(request with { Epoch=Guid.NewGuid().ToString("N") }));
        Assert.Throws<UnauthorizedAccessException>(()=>a.Receiver.CompleteCatalogRetirements(request with { Scope=new([999]) }));
        var response=a.Receiver.CompleteCatalogRetirements(request);
        Assert.Equal("completed",Assert.Single(response.Results).Result.State);
        var revision=Notification(a.Publisher.PublisherId);
        Assert.Equal(Assert.Single(response.Results),Assert.Single(a.Receiver.CompleteCatalogRetirements(request).Results));
        Assert.Equal(revision,Notification(a.Publisher.PublisherId));
        Assert.Empty(a.Receiver.ReadCatalogCompletions(read).Items);
        Assert.Single(b.Receiver.ReadCatalogRetirements(new(b.Publisher.PublisherId,b.Publisher.Epoch,new([77]))).Anchors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetiredIdentityRejectsLateCatalogLinksAndEditsWithoutTouchingAnotherSource(bool shared)
    {
        var a=Seed();var other=shared?Seed():a;
        var anchor=new CatalogRetirementAnchor(Guid.NewGuid().ToString("N"),"tag",a.CatalogGlobal,"image",a.MediaGlobal,77)
            { Fingerprint=_database.GetCatalogRetirementFingerprint("tag",a.Tag,"image",a.Image) };
        var source=_database.GetImage(a.Image)!.SourceId;
        a.Receiver.ApplyCatalogRetirements(new(a.Publisher.PublisherId,a.Publisher.Epoch,new([77]),[anchor]));
        using(var transaction=_database.BeginTransaction())
        {
            var result=_database.FinalizeCatalogRetirement(a.Publisher.PublisherId,anchor.OperationId,"tag",a.CatalogGlobal,1,new([source]),transaction);
            Assert.Equal("completed",result.State);Assert.Equal(!shared,result.CatalogRemoved);transaction.Commit();
        }
        Assert.Equal(200,a.Receiver.Apply(new(a.Publisher.PublisherId,a.Publisher.Epoch,3,Guid.NewGuid().ToString("N"),"catalog",
            JsonSerializer.SerializeToElement(new SpaceCatalogBatch([new Tag { Id=800,Name="旧快照名称",CreatedUtc=DateTime.UtcNow }],[],[])),
            [new("tag",800,a.CatalogGlobal)])).Status);
        Assert.Equal(200,a.Receiver.Apply(new(a.Publisher.PublisherId,a.Publisher.Epoch,4,Guid.NewGuid().ToString("N"),"metadata",
            JsonSerializer.SerializeToElement(new SpaceMetadataBatch(new Picer.Core.Gateway.GatewayMetadata([],[new(700,800)],[]))),[])).Status);
        var edit=new MetadataEdit("tag-link",700,"800","1",DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N"));
        Assert.Equal(200,a.Receiver.Apply(new(a.Publisher.PublisherId,a.Publisher.Epoch,5,Guid.NewGuid().ToString("N"),"metadata-edits",
            JsonSerializer.SerializeToElement(new SpaceMetadataEditsBatch([edit])),[])).Status);
        Assert.Empty(SemanticEngine.GetImageTags(_database,a.Image));
        if(shared)
        {
            Assert.Equal("共享测试标签",Assert.Single(SemanticEngine.GetImageTags(_database,other.Image)).Name);
            Assert.False(_database.IsRetiredCatalogIdentity(other.Publisher.PublisherId,"tag",other.CatalogGlobal));
        }
        else Assert.Empty(SemanticEngine.GetTags(_database));

        // Reuse the local integer ID with a new generation. Frozen old references
        // must still address the retired generation after the mapping changes.
        var replacement=Guid.NewGuid().ToString("N");
        var replacementName=shared?"共享测试标签":"新标签";
        SpaceSyncBatch Batch(long sequence,string kind,object payload,string? global)=>
            new(a.Publisher.PublisherId,a.Publisher.Epoch,sequence,Guid.NewGuid().ToString("N"),kind,
                JsonSerializer.SerializeToElement(payload),kind=="catalog"?[new("tag",800,global!)]:[])
            { CatalogReferences=global is null?null:[new("tag",800,global)] };
        Assert.Equal(200,a.Receiver.Apply(Batch(6,"catalog",new SpaceCatalogBatch(
            [new Tag { Id=800,Name=replacementName,CreatedUtc=DateTime.UtcNow }],[],[]),replacement)).Status);
        var recreate=edit with { OperationId=Guid.NewGuid().ToString("N") };
        Assert.Equal(200,a.Receiver.Apply(Batch(7,"metadata-edits",new SpaceMetadataEditsBatch([recreate]),replacement)).Status);
        Assert.Equal(replacementName,Assert.Single(SemanticEngine.GetImageTags(_database,a.Image)).Name);
        Assert.Equal(200,a.Receiver.Apply(Batch(8,"catalog",new SpaceCatalogBatch(
            [new Tag { Id=800,Name="旧快照不能改名",CreatedUtc=DateTime.UtcNow }],[],[]),a.CatalogGlobal)).Status);
        Assert.Equal(200,a.Receiver.Apply(Batch(9,"metadata",new SpaceMetadataBatch(
            new Picer.Core.Gateway.GatewayMetadata([],[new(700,800)],[])),a.CatalogGlobal)).Status);
        Assert.Equal(200,a.Receiver.Apply(Batch(10,"metadata-edits",new SpaceMetadataEditsBatch(
            [edit with { Value="0",OperationId=Guid.NewGuid().ToString("N") }]),a.CatalogGlobal)).Status);
        Assert.Equal(422,a.Receiver.Apply(Batch(11,"metadata-edits",new SpaceMetadataEditsBatch(
            [edit with { Value="0",OperationId=Guid.NewGuid().ToString("N") }]),null)).Status);
        Assert.Equal(replacementName,Assert.Single(SemanticEngine.GetImageTags(_database,a.Image)).Name);
        // Repeat the same source operation in another batch: the original causal
        // request must remain stable despite the confirmed predecessor version.
        Assert.Equal(200,a.Receiver.Apply(Batch(12,"metadata-edits",new SpaceMetadataEditsBatch([recreate]),replacement)).Status);
        var activeTag=Assert.Single(SemanticEngine.GetImageTags(_database,a.Image)).Id;
        SemanticEngine.RemoveTag(_database,a.Image,activeTag);
        Assert.Equal(200,a.Receiver.Apply(Batch(13,"metadata-edits",new SpaceMetadataEditsBatch(
            [recreate with { OperationId=Guid.NewGuid().ToString("N") }]),replacement)).Status);
        Assert.Empty(SemanticEngine.GetImageTags(_database,a.Image));
        Assert.NotEmpty(_database.GetMetadataEditConflicts());
    }

    [Fact]
    public void ReceiverChecksDeviceEpochAndSourceAndRollsBackAnInvalidBatch()
    {
        var a=Seed();var b=Seed();
        var scope=new SpaceUploadScope([77]);
        var anchor=new CatalogRetirementAnchor(Guid.NewGuid().ToString("N"),"tag",a.CatalogGlobal,"image",a.MediaGlobal,-999)
            { Fingerprint=_database.GetCatalogRetirementFingerprint("tag",a.Tag,"image",a.Image) };
        var request=new SpaceCatalogRetirementRequest(a.Publisher.PublisherId,a.Publisher.Epoch,scope,[anchor]);
        Assert.Throws<UnauthorizedAccessException>(()=>b.Receiver.ApplyCatalogRetirements(request));
        Assert.Throws<InvalidOperationException>(()=>a.Receiver.ApplyCatalogRetirements(request with { Epoch=Guid.NewGuid().ToString("N") }));
        foreach(var denied in new[]{a.Device with { Scope="index.read" },a.Device with { Scope="source.serve",Kind="agent" },
            a.Device with { GrantId="other-grant" },a.Device with { SpaceId="other-space" }})
            Assert.Throws<UnauthorizedAccessException>(()=>new SpaceSyncReceiver(_database,denied).ApplyCatalogRetirements(request));
        Assert.Throws<UnauthorizedAccessException>(()=>a.Receiver.ApplyCatalogRetirements(request with { Scope=new([999]) }));
        Assert.Throws<UnauthorizedAccessException>(()=>a.Receiver.ApplyCatalogRetirements(request with
            { Anchors=[anchor,anchor with { OperationId=Guid.NewGuid().ToString("N"),MediaGlobalId=b.MediaGlobal }] }));
        Assert.Single(SemanticEngine.GetImageTags(_database,a.Image));
        using(var receipts=_database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_receipts")) Assert.Equal(0L,receipts.ExecuteScalar());
        var response=a.Receiver.ApplyCatalogRetirements(request);
        Assert.Equal("applied",Assert.Single(response.Results).Result.State);
        Assert.Equal(a.Publisher.PublisherId,response.PublisherId);Assert.Equal(a.Publisher.Epoch,response.Epoch);
        var revision=Notification(a.Publisher.PublisherId);
        Assert.Equal(Assert.Single(response.Results),Assert.Single(a.Receiver.ApplyCatalogRetirements(request).Results));
        Assert.Equal(revision,Notification(a.Publisher.PublisherId));
        Assert.Empty(SemanticEngine.GetImageTags(_database,a.Image));Assert.Single(SemanticEngine.GetImageTags(_database,b.Image));
        using var suppression=_database.Command("SELECT COUNT(*) FROM sync_suppression");Assert.Equal(0L,suppression.ExecuteScalar());
    }

    [Fact]
    public void RetirementPagesResolveLocalRootsAndNeverReturnAnotherPublishersAnchors()
    {
        var a=Seed();var b=Seed();var extra=Guid.NewGuid().ToString("N");
        var upload=new SpaceSyncBatch(a.Publisher.PublisherId,a.Publisher.Epoch,3,Guid.NewGuid().ToString("N"),"images",
            JsonSerializer.SerializeToElement(new SpaceImageBatch(77,@"C:\Photos",[Photo(701,"two.jpg")])),[new("image",701,extra)]);
        Assert.Equal(200,a.Receiver.Apply(upload).Status);
        var extraImage=Target(a.Publisher.PublisherId,"image",extra);
        SemanticEngine.AssignTag(_database,extraImage,a.Tag);
        SemanticEngine.DeleteTag(_database,a.Tag);
        var request=new SpaceCatalogRetirementReadRequest(a.Publisher.PublisherId,a.Publisher.Epoch,new([77]),PageSize:1);
        var first=a.Receiver.ReadCatalogRetirements(request);Assert.True(first.HasMore);
        var second=a.Receiver.ReadCatalogRetirements(request with { After=first.Next });Assert.False(second.HasMore);
        var returned=first.Anchors.Concat(second.Anchors).Select(x=>x.MediaGlobalId).ToHashSet();
        Assert.True(returned.SetEquals(new[]{a.MediaGlobal,extra}));Assert.DoesNotContain(b.MediaGlobal,returned);
        Assert.Single(b.Receiver.ReadCatalogRetirements(new(b.Publisher.PublisherId,b.Publisher.Epoch,new([77]))).Anchors);
        Assert.Empty(a.Receiver.ReadCatalogRetirements(request with { Scope=new([999]) }).Anchors);
        Assert.Throws<UnauthorizedAccessException>(()=>b.Receiver.ReadCatalogRetirements(request));
        Assert.Throws<ArgumentException>(()=>a.Receiver.ReadCatalogRetirements(request with { After=new(null!,"image","x") }));
        var item=Assert.Single(first.Anchors);
        var acknowledgement=new SpaceCatalogRetirementDeliveryRequest(a.Publisher.PublisherId,a.Publisher.Epoch,new([77]),
            [new(item,new("applied",item.Fingerprint!))]);
        Assert.Throws<UnauthorizedAccessException>(()=>b.Receiver.AcknowledgeCatalogRetirements(acknowledgement));
        Assert.Throws<UnauthorizedAccessException>(()=>a.Receiver.AcknowledgeCatalogRetirements(acknowledgement with { Scope=new([999]) }));
        Assert.Throws<InvalidDataException>(()=>a.Receiver.AcknowledgeCatalogRetirements(acknowledgement with
            { Items=[new(item with { OperationId=Guid.NewGuid().ToString("N") },new("applied",item.Fingerprint!))] }));
        Assert.Equal(item.Cursor,Assert.Single(a.Receiver.AcknowledgeCatalogRetirements(acknowledgement).Accepted));
        var revision=Notification(a.Publisher.PublisherId);
        a.Receiver.AcknowledgeCatalogRetirements(acknowledgement);
        Assert.Equal(revision,Notification(a.Publisher.PublisherId));
        a.Receiver.AcknowledgeCatalogRetirements(acknowledgement with { Items=[new(item,new("conflict",item.Fingerprint!))] });
        Assert.Equal(revision,Notification(a.Publisher.PublisherId));
        var remaining=Assert.Single(a.Receiver.ReadCatalogRetirements(request).Anchors);
        Assert.NotEqual(item.MediaGlobalId,remaining.MediaGlobalId);
        Assert.Single(b.Receiver.ReadCatalogRetirements(new(b.Publisher.PublisherId,b.Publisher.Epoch,new([77]))).Anchors);
    }

    private long Notification(string publisher)
    {
        using var query=_database.Command("SELECT revision FROM sync_publisher_notifications WHERE publisher_id=$publisher");
        query.Parameters.AddWithValue("$publisher",publisher);return Convert.ToInt64(query.ExecuteScalar());
    }

    private (SpaceSyncReceiver Receiver,SpacePublisher Publisher,AuthorizedDevice Device,string CatalogGlobal,string MediaGlobal,long Image,long Tag) Seed()
    {
        var device=new AuthorizedDevice(Guid.NewGuid().ToString("N"),"account","space",Guid.NewGuid().ToString("N"),"public","gui",AccountStore.EditorScopes,1,1);
        var receiver=new SpaceSyncReceiver(_database,device);var publisher=receiver.Register(new(Guid.NewGuid().ToString("N"),device.DeviceId));
        var media=Guid.NewGuid().ToString("N");var catalog=Guid.NewGuid().ToString("N");
        Assert.Equal(200,receiver.Apply(new(publisher.PublisherId,publisher.Epoch,1,Guid.NewGuid().ToString("N"),"images",
            JsonSerializer.SerializeToElement(new SpaceImageBatch(77,@"C:\Photos",[Photo(700,"one.jpg")])),[new("image",700,media)])).Status);
        Assert.Equal(200,receiver.Apply(new(publisher.PublisherId,publisher.Epoch,2,Guid.NewGuid().ToString("N"),"catalog",
            JsonSerializer.SerializeToElement(new SpaceCatalogBatch([new Tag { Id=800,Name="共享测试标签",CreatedUtc=DateTime.UtcNow }],[],[])),[new("tag",800,catalog)])).Status);
        var image=Target(publisher.PublisherId,"image",media);var tag=Target(publisher.PublisherId,"tag",catalog);
        SemanticEngine.AssignTag(_database,image,tag);return(receiver,publisher,device,catalog,media,image,tag);
    }

    private long Target(string publisher,string kind,string global)
    {
        using var query=_database.Command("SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND global_id=$global");
        query.Parameters.AddWithValue("$publisher",publisher);query.Parameters.AddWithValue("$kind",kind);query.Parameters.AddWithValue("$global",global);
        return Convert.ToInt64(query.ExecuteScalar());
    }

    private static ImageRecord Photo(long id,string name)=>new() { Id=id,SourceId=77,Path=@"C:\Photos\"+name,
        FileSize=10,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow };

    public void Dispose()
    {
        _database.Dispose();SqliteConnection.ClearAllPools();if(Directory.Exists(_root)) Directory.Delete(_root,true);
    }
}
