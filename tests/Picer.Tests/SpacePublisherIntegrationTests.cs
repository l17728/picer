using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.People;
using Picer.IndexService;
using Picer.IndexService.Identity;
using Xunit;

namespace Picer.Tests;

public sealed partial class SpacePublisherIntegrationTests
{
    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public async Task AccountSourcePurgeUsesStableIdentityAndRecoversWithoutTouchingAnotherSourceOrOriginal(bool video,bool disconnect)
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-source-purge-"+Guid.NewGuid().ToString("N"));
        const string origin="https://picer.test";
        var instance=AccountServiceHost.Build(Path.Combine(root,"service"),origin,0);
        try
        {
            var address=await IndexServiceHost.StartAsync(instance.App);
            var owner=instance.Accounts.Register("source-purge-user","test-source-purge-password!","来源清除测试");
            var space=Assert.Single(instance.Accounts.ListSpaces(owner));
            using var signing=IdentitySigningKey.LoadOrCreate(instance.Accounts.Root,instance.Accounts.ServiceId);
            var pin=Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            async Task<(DeviceAccountClient Client,AddressBridge Bridge)> Open()
            {
                var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var request=Guid.NewGuid().ToString("N");
                instance.Accounts.CreateEnrollment(new(request,DeviceAuthorization.ExportPublicKey(key),"来源清除设备","gui"));
                var document=instance.Accounts.ApproveEnrollment(owner,request,space.Id,origin,pin,signing,true,false);
                var connection=new DeviceConnection(origin,instance.Accounts.ServiceId,pin,DeviceAuthorization.ExportPublicKey(signing),"test",request,"来源清除设备","gui");
                var bridge=new AddressBridge(address,DeviceAccountClient.CreateTransport(pin));
                var client=new DeviceAccountClient(connection,key,value=>connection=value,bridge);
                await client.ImportAsync(DeviceAuthorization.WriteFileDocument(document));return(client,bridge);
            }
            var first=await Open();var second=await Open();using var clientA=first.Client;using var clientB=second.Client;
            using var localA=new PicerDatabase(Path.Combine(root,"a","picer.db"));
            using var localB=new PicerDatabase(Path.Combine(root,"b","picer.db"));
            var mediaRoot=Path.Combine(root,"media");Directory.CreateDirectory(mediaRoot);
            var path=Path.Combine(mediaRoot,video?"same.mp4":"same.jpg");
            var original=new byte[]{11,22,33,44};await File.WriteAllBytesAsync(path,original);
            foreach(var database in new[]{localA,localB})
            {
                var source=database.AddSource(mediaRoot);
                if(video) new Picer.Core.Video.VideoRepository(database).UpsertMirrored(new(1,source.Id,path,4,DateTime.UnixEpoch,1,10,10,null,null,"active"),source.Id);
                else database.UpsertMirroredImage(new ImageRecord { Id=1,SourceId=source.Id,Path=path,FileSize=4,ModifiedUtc=DateTime.UnixEpoch,
                    IndexedUtc=DateTime.UtcNow,Status=database==localA?ImageStatus.Missing:ImageStatus.Active });
            }
            var scopeA=new SpaceUploadScope(localA.GetSources().Select(source=>source.Id).ToArray());
            var scopeB=new SpaceUploadScope(localB.GetSources().Select(source=>source.Id).ToArray());
            var publisherA=new SpaceSyncPublisher(clientA,localA,Path.Combine(root,"snapshots-a"));
            var publisherB=new SpaceSyncPublisher(clientB,localB,Path.Combine(root,"snapshots-b"));
            await publisherA.PublishAsync(scope:scopeA);await publisherB.PublishAsync(scope:scopeB);
            using var remote=new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id),"picer.db"));
            var kind=video?"video":"image";var table=video?"videos":"images";
            using(var count=remote.Command($"SELECT COUNT(*) FROM {table}")) Assert.Equal(2L,count.ExecuteScalar());
            using(var identity=localB.Command("SELECT p.publisher_id,p.epoch,e.global_id FROM sync_publishers p JOIN sync_entity_identities e ON e.publisher_id=p.publisher_id WHERE e.kind=$kind"))
            {
                identity.Parameters.AddWithValue("$kind",kind);using var row=identity.ExecuteReader();Assert.True(row.Read());
                using var denied=await clientA.SpaceClient.PostAsJsonAsync("api/sync/v2/media-reference",
                    new SpaceMediaReferenceRequest(row.GetString(0),row.GetString(1),scopeB,kind,row.GetString(2)),DeviceAuthorization.Json);
                Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(()=>publisherA.PurgePublishedReferenceAsync(kind,1,scopeA with { Photos=false,Videos=false }));
            string? cache=null;
            if(video)
            {
                cache=VideoRecordPurger.SourceCacheDirectory(path);Directory.CreateDirectory(cache);
                await File.WriteAllTextAsync(Path.Combine(cache,"generated-frame.jpg"),"generated test cache");
            }
            first.Bridge.FailNextCommittedPurge=disconnect;
            if(disconnect)
            {
                await Assert.ThrowsAsync<HttpRequestException>(()=>publisherA.PurgePublishedReferenceAsync(kind,1,scopeA));
                publisherA=new SpaceSyncPublisher(clientA,localA,Path.Combine(root,"snapshots-a"));
                await publisherA.PublishAsync(scope:scopeA);
            }
            else Assert.True(await publisherA.PurgePublishedReferenceAsync(kind,1,scopeA));
            using(var count=localA.Command($"SELECT COUNT(*) FROM {table}")) Assert.Equal(0L,count.ExecuteScalar());
            using(var count=remote.Command($"SELECT COUNT(*) FROM {table}")) Assert.Equal(1L,count.ExecuteScalar());
            await publisherB.PublishAsync(scope:scopeB);await publisherB.PullMetadataChangesAsync(scope:scopeB);
            using(var count=localB.Command($"SELECT COUNT(*) FROM {table}")) Assert.Equal(1L,count.ExecuteScalar());
            Assert.Equal(original,await File.ReadAllBytesAsync(path));
            if(cache is not null) Assert.False(Directory.Exists(cache));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root,"snapshots-a"),"*.db"));
            Assert.Equal(0,await publisherA.PublishAsync(scope:scopeA));
        }
        finally
        {
            await instance.App.StopAsync();await instance.App.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root)) Directory.Delete(root,true);
        }
    }

    [Theory]
    [InlineData(false,false,false)]
    [InlineData(true,false,false)]
    [InlineData(false,true,false)]
    [InlineData(false,false,true)]
    [InlineData(false,true,false,true)]
    [InlineData(false,false,false,false,true)]
    [InlineData(false,true,false,false,false,true)]
    public async Task RetiredTagKeptForPrivatePhotosCanStartANewPublishedGeneration(bool remoteReadd,bool interruptCompletion,bool reviewKeep,bool interruptCompaction=false,bool completionRace=false,bool rebaselineDuringRecovery=false)
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-catalog-generation-"+Guid.NewGuid().ToString("N"));
        const string origin="https://picer.test";
        var instance=AccountServiceHost.Build(Path.Combine(root,"service"),origin,0);
        try
        {
            var address=await IndexServiceHost.StartAsync(instance.App);
            var owner=instance.Accounts.Register("generation-user","test-generation-password!","分类换代测试");
            var space=Assert.Single(instance.Accounts.ListSpaces(owner));
            using var signing=IdentitySigningKey.LoadOrCreate(instance.Accounts.Root,instance.Accounts.ServiceId);
            var pin=Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var request=Guid.NewGuid().ToString("N");
            instance.Accounts.CreateEnrollment(new(request,DeviceAuthorization.ExportPublicKey(key),"分类换代设备","gui"));
            var document=instance.Accounts.ApproveEnrollment(owner,request,space.Id,origin,pin,signing,true,false);
            var connection=new DeviceConnection(origin,instance.Accounts.ServiceId,pin,DeviceAuthorization.ExportPublicKey(signing),"test",request,"分类换代设备","gui");
            var bridge=new AddressBridge(address,DeviceAccountClient.CreateTransport(pin));
            using var client=new DeviceAccountClient(connection,key,value=>connection=value,bridge);
            await client.ImportAsync(DeviceAuthorization.WriteFileDocument(document));
            using var local=new PicerDatabase(Path.Combine(root,"local","picer.db"));
            var source=local.AddSource(@"C:\Photos");var excluded=local.AddSource(@"C:\Private");
            foreach(var (id,sourceId,path) in new[]{(10L,source.Id,@"C:\Photos\one.jpg"),(20L,excluded.Id,@"C:\Private\two.jpg")})
                local.UpsertMirroredImage(new ImageRecord { Id=id,SourceId=sourceId,Path=path,FileSize=100,
                    ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
            var tag=SemanticEngine.AddTag(local,"保留私有照片的标签").Id;
            SemanticEngine.AssignTag(local,10,tag);SemanticEngine.AssignTag(local,20,tag);
            var publisher=new SpaceSyncPublisher(client,local,Path.Combine(root,"snapshots"));
            var scope=new SpaceUploadScope([source.Id],People:false,PreciseLocation:false);
            if(rebaselineDuringRecovery)
                local.UpsertMirroredImage(new ImageRecord { Id=40,SourceId=source.Id,Path=@"C:\Photos\temporary.jpg",FileSize=10,
                    ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
            await publisher.PublishAsync(scope:scope);await publisher.PullMetadataChangesAsync(scope:scope);
            string publisherId;
            using(var query=local.Command("SELECT publisher_id FROM sync_publishers")) publisherId=(string)query.ExecuteScalar()!;
            using var remote=new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id),"picer.db"));
            var remoteScope=new SpaceUploadScope(remote.GetSources().Select(s=>s.Id).ToArray());
            SemanticEngine.DeleteTag(remote,Assert.Single(SemanticEngine.GetTags(remote)).Id);
            var anchor=Assert.Single(remote.GetPendingCatalogRetirementAnchors(publisherId,remoteScope));
            if(completionRace)
            {
                var edited=false;
                bridge.AfterCommittedCatalog=path=>
                {
                    if(edited || !path.EndsWith("/catalog-retirements/complete",StringComparison.Ordinal)) return;
                    edited=true;SemanticEngine.AssignTag(local,10,tag);
                };
            }
            if(reviewKeep)
            {
                SemanticEngine.RemoveTag(local,10,tag);SemanticEngine.AssignTag(local,10,tag);
                await publisher.SynchronizeCatalogRetirementsAsync(scope);
                var review=Assert.Single(local.GetCatalogRetirementReviews());
                local.ResolveCatalogRetirementReview(new(review,true),scope);
                await publisher.PublishAsync(scope:scope);
            }
            if(interruptCompletion)
            {
                bridge.FailAfterCommittedCatalogPath=interruptCompaction?"/api/sync/v2/catalog-retirements/compact":"/api/sync/v2/catalog-retirements/complete";
                await Assert.ThrowsAsync<HttpRequestException>(()=>publisher.SynchronizeCatalogRetirementsAsync(scope));
                using(var pending=local.Command("SELECT COUNT(*) FROM sync_catalog_completion_inbox")) Assert.Equal(interruptCompaction?0L:1L,pending.ExecuteScalar());
                publisher=new SpaceSyncPublisher(client,local,Path.Combine(root,"snapshots"));
                if(rebaselineDuringRecovery)
                {
                    var temporary=remote.GetImages().Single(image=>image.Path.EndsWith("temporary.jpg",StringComparison.Ordinal)).Id;
                    using(var missing=remote.Command("UPDATE images SET status='missing' WHERE id=$id"))
                    { missing.Parameters.AddWithValue("$id",temporary);missing.ExecuteNonQuery(); }
                    using var gateway=new Picer.Core.Gateway.HttpIndexGateway(client.SpaceClient);
                    await gateway.GetChangesAsync(new());Assert.True(await gateway.PurgeMissingImageAsync(temporary));
                    await publisher.PublishAsync(scope:scope);Assert.Null(local.GetImage(40));
                }
            }
            await publisher.SynchronizeCatalogRetirementsAsync(scope);
            if(completionRace)
            {
                var review=Assert.Single(local.GetCatalogRetirementReviews());Assert.True(review.CompletionOnly);
                Assert.False(local.IsRetiredCatalogIdentity(publisherId,"tag",anchor.GlobalId));
                Assert.True(remote.IsRetiredCatalogIdentity(publisherId,"tag",anchor.GlobalId));
                local.ResolveCatalogRetirementReview(new(review,true),scope);
                await publisher.PublishAsync(scope:scope);
            }
            using(var pending=local.Command("SELECT COUNT(*) FROM sync_catalog_completion_inbox")) Assert.Equal(0L,pending.ExecuteScalar());
            Assert.True(local.IsRetiredCatalogIdentity(publisherId,"tag",anchor.GlobalId));
            Assert.True(remote.IsRetiredCatalogIdentity(publisherId,"tag",anchor.GlobalId));
            Assert.Empty(local.GetCatalogCompactionCandidates(publisherId,scope));
            Assert.Empty(remote.GetCatalogCompactionCandidates(publisherId,remoteScope));
            Assert.Equal(reviewKeep||completionRace?1:0,SemanticEngine.GetImageTags(local,10).Count);Assert.Single(SemanticEngine.GetImageTags(local,20));
            if(remoteReadd)
            {
                var recreated=SemanticEngine.AddTag(remote,"保留私有照片的标签").Id;
                SemanticEngine.AssignTag(remote,Assert.Single(remote.GetImages()).Id,recreated);
                await publisher.PullMetadataChangesAsync(scope:scope);
                Assert.Single(SemanticEngine.GetImageTags(local,10));
            }
            else if(!reviewKeep&&!completionRace) SemanticEngine.AssignTag(local,10,tag);
            await publisher.PublishAsync(scope:scope);await publisher.PullMetadataChangesAsync(scope:scope);
            var remoteImage=Assert.Single(remote.GetImages());
            Assert.Single(SemanticEngine.GetImageTags(remote,remoteImage.Id));
            Assert.Single(SemanticEngine.GetImageTags(local,10));Assert.Single(SemanticEngine.GetImageTags(local,20));
            Assert.Empty(local.GetMetadataEditConflicts());Assert.Empty(remote.GetMetadataEditConflicts());
            using(var identity=local.Command("SELECT global_id FROM sync_entity_identities WHERE kind='tag' AND publisher_id=$publisher"))
            { identity.Parameters.AddWithValue("$publisher",publisherId);Assert.NotEqual(anchor.GlobalId,identity.ExecuteScalar()); }
            Assert.Equal(0,await publisher.PublishAsync(scope:scope));
        }
        finally
        {
            await instance.App.StopAsync();await instance.App.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root)) Directory.Delete(root,true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false,true)]
    public async Task DeletingCatalogDuringFirstUploadKeepsAllAuthorizedRetirementAnchors(bool album,bool remoteKeep=false)
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-catalog-upload-"+Guid.NewGuid().ToString("N"));
        const string origin="https://picer.test";
        var instance=AccountServiceHost.Build(Path.Combine(root,"service"),origin,0);
        try
        {
            var address=await IndexServiceHost.StartAsync(instance.App);
            var owner=instance.Accounts.Register("catalog-user","test-catalog-password!","上传时删除测试");
            var space=Assert.Single(instance.Accounts.ListSpaces(owner));
            using var signing=IdentitySigningKey.LoadOrCreate(instance.Accounts.Root,instance.Accounts.ServiceId);
            var pin=Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var request=Guid.NewGuid().ToString("N");
            instance.Accounts.CreateEnrollment(new(request,DeviceAuthorization.ExportPublicKey(key),"分类测试设备","gui"));
            var document=instance.Accounts.ApproveEnrollment(owner,request,space.Id,origin,pin,signing,true,false);
            var connection=new DeviceConnection(origin,instance.Accounts.ServiceId,pin,DeviceAuthorization.ExportPublicKey(signing),"test",request,"分类测试设备","gui");
            var bridge=new AddressBridge(address,DeviceAccountClient.CreateTransport(pin));
            using var client=new DeviceAccountClient(connection,key,value=>connection=value,bridge);
            await client.ImportAsync(DeviceAuthorization.WriteFileDocument(document));
            using var local=new PicerDatabase(Path.Combine(root,"local","picer.db"));
            var source=local.AddSource(@"C:\Photos");var excluded=local.AddSource(@"C:\Private");
            foreach(var (id,sourceId) in new[]{(10L,source.Id),(11L,source.Id),(12L,excluded.Id)})
                local.UpsertMirroredImage(new ImageRecord { Id=id,SourceId=sourceId,Path=@"C:\Photos\"+id+".jpg",
                    FileSize=100,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
            var catalog=album?SemanticEngine.AddAlbum(local,"测试相册").Id:SemanticEngine.AddTag(local,"测试标签").Id;
            foreach(var id in new[]{10L,11L,12L})
                if(album) SemanticEngine.AddToAlbum(local,catalog,id);else SemanticEngine.AssignTag(local,id,catalog);
            var deleted=false;
            bridge.AfterCommittedBatch=batch=>
            {
                if(deleted || batch.Kind!="sources") return;
                deleted=true;
                if(album) SemanticEngine.DeleteAlbum(local,catalog);else SemanticEngine.DeleteTag(local,catalog);
            };
            var publisher=new SpaceSyncPublisher(client,local,Path.Combine(root,"snapshots"));
            var scope=new SpaceUploadScope([source.Id],People:false,PreciseLocation:false);
            await publisher.PublishAsync(scope:scope);
            Assert.True(deleted);
            string publisherId;
            using(var query=local.Command("SELECT publisher_id FROM sync_publishers")) publisherId=(string)query.ExecuteScalar()!;
            var anchors=local.GetPendingCatalogRetirementAnchors(publisherId,scope);
            Assert.Equal(2,anchors.Count);
            // The old frozen snapshot is now uploaded, but deletion happened later.
            // Do not publish retirement before the subsequent metadata cut is sent.
            Assert.Empty(local.GetPendingCatalogRetirementAnchors(publisherId,scope,requirePublishedCut:true));
            Assert.All(anchors,anchor=>
            {
                Assert.Equal(album?"album":"tag",anchor.Kind);
                Assert.Equal(source.Id,anchor.SourceId);
                Assert.StartsWith("v2:",anchor.Fingerprint);
            });
            using(var query=local.Command("SELECT COUNT(*) FROM sync_entity_identities WHERE kind='image' AND local_key='12'"))
                Assert.Equal(0L,query.ExecuteScalar());
            using var remote=new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id),"picer.db"));
            Assert.Equal(2,remote.GetImages().Count());
            foreach(var anchor in anchors)
            {
                using var query=remote.Command("SELECT COUNT(*) FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='image' AND global_id=$global");
                query.Parameters.AddWithValue("$publisher",publisherId);query.Parameters.AddWithValue("$global",anchor.MediaGlobalId);
                Assert.Equal(1L,query.ExecuteScalar());
            }
            if(remoteKeep)
            {
                var remoteTag=Assert.Single(SemanticEngine.GetTags(remote)).Id;
                var remoteImage=remote.GetImages().OrderBy(image=>image.Id).First().Id;
                SemanticEngine.RemoveTag(remote,remoteImage,remoteTag);SemanticEngine.AssignTag(remote,remoteImage,remoteTag);
            }
            bridge.FailAfterCommittedCatalogPath="/api/sync/v2/catalog-retirements/apply";
            await Assert.ThrowsAsync<HttpRequestException>(()=>publisher.PublishAsync(scope:scope));
            await publisher.SynchronizeCatalogRetirementsAsync(scope);
            if(remoteKeep)
            {
                using var reviews=await client.SpaceClient.PostAsJsonAsync("api/sync/v2/catalog-conflicts",new { });
                reviews.EnsureSuccessStatusCode();
                var review=Assert.Single((await reviews.Content.ReadFromJsonAsync<CatalogRetirementReview[]>(DeviceAuthorization.Json))!);
                using var decision=await client.SpaceClient.PostAsJsonAsync("api/sync/v2/catalog-conflicts/resolve",new CatalogRetirementDecision(review,true),DeviceAuthorization.Json);
                decision.EnsureSuccessStatusCode();
                await publisher.PublishAsync(scope:scope);await publisher.PullMetadataChangesAsync(scope:scope);
                Assert.Single(SemanticEngine.GetTags(local));Assert.Single(SemanticEngine.GetTags(remote));
                Assert.Equal(1,local.GetImages().Sum(image=>SemanticEngine.GetImageTags(local,image.Id).Count));
                Assert.Equal(0,local.CountPendingCatalogSynchronization());Assert.Equal(0,remote.CountPendingCatalogSynchronization());
            }
            Assert.Empty(local.GetPendingCatalogRetirementAnchors(publisherId,scope));
            Assert.Empty(local.GetReadyCatalogRetirements(publisherId,scope));
            if(album) Assert.Empty(SemanticEngine.GetAlbums(remote));else if(!remoteKeep) Assert.Empty(SemanticEngine.GetTags(remote));
        }
        finally
        {
            await instance.App.StopAsync();await instance.App.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root)) Directory.Delete(root,true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PhotoClassificationRoundTripsThroughAuthenticatedApiAndResumesPhotoScope(bool verifiedHash)
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-photo-api-"+Guid.NewGuid().ToString("N"));
        const string origin="https://picer.test";
        var instance=AccountServiceHost.Build(Path.Combine(root,"service"),origin,0);
        try
        {
            var address=await IndexServiceHost.StartAsync(instance.App);
            var owner=instance.Accounts.Register("photo-user","test-photo-password!","照片同步测试");
            var space=Assert.Single(instance.Accounts.ListSpaces(owner));
            using var signing=IdentitySigningKey.LoadOrCreate(instance.Accounts.Root,instance.Accounts.ServiceId);
            var pin=Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var request=Guid.NewGuid().ToString("N");
            instance.Accounts.CreateEnrollment(new(request,DeviceAuthorization.ExportPublicKey(key),"照片测试设备","gui"));
            var document=instance.Accounts.ApproveEnrollment(owner,request,space.Id,origin,pin,signing,true,false);
            var connection=new DeviceConnection(origin,instance.Accounts.ServiceId,pin,DeviceAuthorization.ExportPublicKey(signing),"test",request,"照片测试设备","gui");
            using var client=new DeviceAccountClient(connection,key,value=>connection=value,new AddressBridge(address,DeviceAccountClient.CreateTransport(pin)));
            await client.ImportAsync(DeviceAuthorization.WriteFileDocument(document));
            using var local=new PicerDatabase(Path.Combine(root,"local","picer.db"));
            var source=local.AddSource(@"C:\Photos");
            local.UpsertMirroredImage(new ImageRecord { Id=7000,SourceId=source.Id,Path=@"C:\Photos\one.jpg",FileSize=100,
                ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow,Sha256=verifiedHash?new string('c',64):null });
            var concepts=new Picer.Core.Vision.VisualConceptRepository(local);
            concepts.Confirm(7000,"scene.mountain");
            concepts.Reject(7000,"cluster.semantic.local-test");
            var publisher=new SpaceSyncPublisher(client,local,Path.Combine(root,"snapshots"));
            var scope=new SpaceUploadScope([source.Id],People:false,PreciseLocation:false);
            await publisher.PublishAsync(scope:scope);
            await publisher.PullMetadataChangesAsync(scope:scope);
            using var remote=new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id),"picer.db"));
            var remoteImage=Assert.Single(remote.GetImages()).Id;
            Assert.NotEqual(7000,remoteImage);
            Assert.All(local.GetMetadataEdits(),edit=>Assert.True(edit.ServerVersion>0));
            Assert.Contains(remote.GetMetadataEdits(),edit=>edit.Field.StartsWith("cluster.semantic.",StringComparison.Ordinal)
                && edit.Field.EndsWith(":local-test",StringComparison.Ordinal));
            using var changed=await client.SpaceClient.PostAsJsonAsync("api/service/v1/classification/reject",
                new ClassificationReviewRequest(remoteImage,"scene.mountain"));
            changed.EnsureSuccessStatusCode();
            await publisher.PullMetadataChangesAsync(scope:scope with { Photos=false });
            Assert.Equal("accepted",local.GetMetadataEdits().Single(edit=>edit.Kind=="photo-classification-policy" && edit.Field=="scene.mountain").Value);
            await publisher.PullMetadataChangesAsync(scope:scope);
            Assert.Equal("rejected",local.GetMetadataEdits().Single(edit=>edit.Kind=="photo-classification-policy" && edit.Field=="scene.mountain").Value);
            Assert.Equal("1",local.GetMetadataEdits().Single(edit=>edit.Kind=="photo-classification-confirmation").Value);
            Assert.Contains(local.GetMetadataEdits(),edit=>edit.Field=="cluster.semantic.local-test");
            Assert.Equal(0,await publisher.PublishAsync(scope:scope));
            var readKey=ECDsa.Create(ECCurve.NamedCurves.nistP256);var readRequest=Guid.NewGuid().ToString("N");
            instance.Accounts.CreateEnrollment(new(readRequest,DeviceAuthorization.ExportPublicKey(readKey),"只读照片设备","gui"));
            var readDocument=instance.Accounts.ApproveEnrollment(owner,readRequest,space.Id,origin,pin,signing,false,true);
            var readConnection=new DeviceConnection(origin,instance.Accounts.ServiceId,pin,DeviceAuthorization.ExportPublicKey(signing),"test",readRequest,"只读照片设备","gui");
            using var readClient=new DeviceAccountClient(readConnection,readKey,value=>readConnection=value,new AddressBridge(address,DeviceAccountClient.CreateTransport(pin)));
            await readClient.ImportAsync(DeviceAuthorization.WriteFileDocument(readDocument));
            using var denied=await readClient.SpaceClient.PostAsJsonAsync("api/service/v1/classification/restore",new ClassificationReviewRequest(remoteImage,"scene.mountain"));
            Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);

            foreach(var id in new[]{7001L,7002L})
                local.UpsertMirroredImage(new ImageRecord { Id=id,SourceId=source.Id,Path=@"C:\Photos\"+id+".jpg",FileSize=100,
                    ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
            concepts.Reject(7001,"scene.beach");
            new Picer.Core.Vision.ClassificationProjectionService(local).RestoreSuggestion(7002,"scene.beach");
            using(var learn=local.Command("UPDATE images SET sha256=$sha WHERE id IN (7001,7002)"))
            { learn.Parameters.AddWithValue("$sha",new string('d',64));learn.ExecuteNonQuery(); }
            Assert.Single(local.GetMetadataEditConflicts());
            await publisher.PublishAsync(scope:scope);await publisher.PullMetadataChangesAsync(scope:scope);
            using var conflictResponse=await client.SpaceClient.PostAsJsonAsync("api/sync/v2/metadata-conflicts",new { });
            conflictResponse.EnsureSuccessStatusCode();
            var conflicts=(await conflictResponse.Content.ReadFromJsonAsync<MetadataConflictSnapshot>(DeviceAuthorization.Json))!;
            var conflict=Assert.Single(conflicts.Reviews).Conflict;
            Assert.Equal("scene.beach",conflict.Proposed.Field);
            using var resolution=await client.SpaceClient.PostAsJsonAsync("api/sync/v2/metadata-conflicts/resolve",
                new MetadataConflictResolution(conflict.OperationId,true,conflict.Current.ServerVersion,conflict.Current.OperationId,conflicts.Epoch));
            resolution.EnsureSuccessStatusCode();
            await publisher.PullMetadataChangesAsync(scope:scope);
            Assert.Empty(local.GetMetadataEditConflicts());
            Assert.Equal(conflict.Proposed.Value,local.GetMetadataEdits().Single(edit=>edit.Field=="scene.beach").Value);
            Assert.Equal(0,await publisher.PublishAsync(scope:scope));
        }
        finally
        {
            await instance.App.StopAsync();await instance.App.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root)) Directory.Delete(root,true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VideoOwnershipReviewUsesAuthenticatedSpaceApiAndRejectsReadOnlyOrStaleRequests(bool verifiedVideoHash)
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-review-api-"+Guid.NewGuid().ToString("N"));
        const string origin="https://picer.test";
        var instance=AccountServiceHost.Build(Path.Combine(root,"service"),origin,0);
        try
        {
            var address=await IndexServiceHost.StartAsync(instance.App);
            var owner=instance.Accounts.Register("review-user","test-review-password!","审核空间");
            var space=Assert.Single(instance.Accounts.ListSpaces(owner));
            using var signing=IdentitySigningKey.LoadOrCreate(instance.Accounts.Root,instance.Accounts.ServiceId);
            var pin=Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            async Task<DeviceAccountClient> Open(bool editing)
            {
                var key=ECDsa.Create(ECCurve.NamedCurves.nistP256); var request=Guid.NewGuid().ToString("N");
                instance.Accounts.CreateEnrollment(new(request,DeviceAuthorization.ExportPublicKey(key),"审核设备","gui"));
                var document=instance.Accounts.ApproveEnrollment(owner,request,space.Id,origin,pin,signing,editing,false);
                var connection=new DeviceConnection(origin,instance.Accounts.ServiceId,pin,DeviceAuthorization.ExportPublicKey(signing),"test",request,"审核设备","gui");
                var client=new DeviceAccountClient(connection,key,value=>connection=value,new AddressBridge(address,DeviceAccountClient.CreateTransport(pin)));
                await client.ImportAsync(DeviceAuthorization.WriteFileDocument(document)); return client;
            }
            using var editor=await Open(true); using var reader=await Open(false);
            var dbPath=Path.Combine(instance.Accounts.SpaceRoot(space.Id),"picer.db");
            using (var database=new PicerDatabase(dbPath))
            {
                foreach (var (id,publisher) in new[]{(10L,'a'),(20L,'b')})
                {
                    var path=PublishedMediaPath.Qualify(new string(publisher,32),@"C:\Videos"); var source=database.AddSource(path).Id;
                    new Picer.Core.Video.VideoRepository(database).UpsertMirrored(new(id,source,path+"\\clip.mp4",10,DateTime.UnixEpoch,null,0,0,null,null,"active")
                        { Sha256=new string('f',64) },source);
                }
                var edit=new MetadataEdit("video-concept",10,"[\"animal\",\"manual\"]","accepted",DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N"))
                    { VideoContentIdentity="sha256:"+new string('f',64) };
                using var seed=database.Command("INSERT INTO video_metadata_ownership_reviews(operation_id,content_id,kind,field,payload) SELECT $op,content_id,$kind,$field,$payload FROM videos WHERE id=10");
                seed.Parameters.AddWithValue("$op",edit.OperationId); seed.Parameters.AddWithValue("$kind",edit.Kind);
                seed.Parameters.AddWithValue("$field",edit.Field); seed.Parameters.AddWithValue("$payload",System.Text.Json.JsonSerializer.Serialize(edit)); seed.ExecuteNonQuery();
            }
            using var listing=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/video-ownership-reviews",new { });
            Assert.Equal(HttpStatusCode.OK,listing.StatusCode);
            var review=Assert.Single((await listing.Content.ReadFromJsonAsync<VideoOwnershipReview[]>(DeviceAuthorization.Json))!);
            Assert.Equal(2,review.Candidates.Count);
            var candidate=review.Candidates.Single(item=>item.VideoId==10);
            var request=new VideoOwnershipResolution(review.OperationId,[new(candidate.VideoId,candidate.Version,candidate.CurrentOperationId)],Epoch:review.Epoch);
            using var denied=await reader.SpaceClient.PostAsJsonAsync("api/sync/v2/video-ownership-reviews/resolve",request);
            Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
            using var stale=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/video-ownership-reviews/resolve",request with { Epoch=Guid.NewGuid().ToString("N") });
            Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
            using var applied=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/video-ownership-reviews/resolve",request);
            Assert.Equal(HttpStatusCode.OK,applied.StatusCode);
            using var retry=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/video-ownership-reviews/resolve",request);
            Assert.Equal(HttpStatusCode.OK,retry.StatusCode);
            using var verified=new PicerDatabase(dbPath);
            Assert.Empty(verified.GetVideoOwnershipReviews());
            var metadata=new Picer.Core.Video.VideoSearchMetadata(verified);
            Assert.Contains("animal",metadata.GetConcepts(10)); Assert.Empty(metadata.GetConcepts(20));

            verified.UpsertMirroredPerson(new(90,"视频审核测试人物",0,0));
            verified.UpsertMirroredVideoMetadata(new Picer.Core.Gateway.GatewayMetadata([],[],[]) with
                { VideoKeyframes=[new(100,10,0,"",0)] });
            using(var seedFace=verified.Command("""
                INSERT INTO video_detected_faces(video_id,keyframe_id,timestamp_seconds,ordinal,model_version,
                    box_x,box_y,box_width,box_height,confidence,assigned_person_id,assignment_state,updated_utc,created_utc)
                SELECT content_id,100,0,0,'test-model',0,0,10,10,1,90,'confirmed',$now,$now FROM videos WHERE id=10;
                """))
            { seedFace.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));seedFace.ExecuteNonQuery(); }
            using(var faceListing=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/video-ownership-reviews",new { }))
            {
                Assert.Equal(HttpStatusCode.OK,faceListing.StatusCode);
                var faceReview=Assert.Single((await faceListing.Content.ReadFromJsonAsync<VideoOwnershipReview[]>(DeviceAuthorization.Json))!);
                Assert.Equal("video-face",faceReview.Kind);
                var faceCandidate=faceReview.Candidates.Single(item=>item.VideoId==20);
                var faceRequest=new VideoOwnershipResolution(faceReview.OperationId,
                    [new(faceCandidate.VideoId,faceCandidate.Version,faceCandidate.CurrentOperationId)],Epoch:faceReview.Epoch);
                using var faceDenied=await reader.SpaceClient.PostAsJsonAsync("api/sync/v2/video-ownership-reviews/resolve",faceRequest);
                Assert.Equal(HttpStatusCode.Forbidden,faceDenied.StatusCode);
                using var faceStale=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/video-ownership-reviews/resolve",
                    faceRequest with { Choices=[new(faceCandidate.VideoId,1,Guid.NewGuid().ToString("N"))] });
                Assert.Equal(HttpStatusCode.Conflict,faceStale.StatusCode);
                using var faceApplied=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/video-ownership-reviews/resolve",faceRequest);
                Assert.Equal(HttpStatusCode.OK,faceApplied.StatusCode);
                using var faceRetry=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/video-ownership-reviews/resolve",faceRequest);
                Assert.Equal(HttpStatusCode.OK,faceRetry.StatusCode);
                Assert.Equal(new long[]{20},metadata.Match([90],[],[]).ToArray());
                Assert.Empty(verified.GetVideoOwnershipReviews());
            }

            using var local = new PicerDatabase(Path.Combine(root, "source", "picer.db"));
            var localSource = local.AddSource(@"C:\SourceVideos");
            new Picer.Core.Video.VideoRepository(local).UpsertMirrored(new(1, localSource.Id, @"C:\SourceVideos\local.mp4",
                10, DateTime.UnixEpoch, null, 0, 0, null, null, "active") { Sha256 = verifiedVideoHash ? new string('d', 64) : null }, localSource.Id);
            var localMetadata = new Picer.Core.Video.VideoSearchMetadata(local);
            localMetadata.SetConcept(1, "landscape", "manual");
            var localOperation = Assert.Single(local.GetMetadataEdits()).OperationId;
            var sourcePublisher = new SpaceSyncPublisher(editor, local, Path.Combine(root, "snapshots"));
            var scope = new SpaceUploadScope([localSource.Id], People: false, PreciseLocation: false);
            await sourcePublisher.PublishAsync(scope: scope);
            await sourcePublisher.PullMetadataChangesAsync(scope: scope);
            Assert.NotEqual(localOperation, Assert.Single(local.GetMetadataEdits()).OperationId);
            using (var previousProtocol = local.Command("UPDATE sync_publishers SET metadata_protocol=2 WHERE state='publishing'"))
                previousProtocol.ExecuteNonQuery();
            using (var previousService = verified.Command("DELETE FROM sync_video_content_identities"))
                previousService.ExecuteNonQuery();
            Assert.True(await sourcePublisher.PublishAsync(scope: scope) > 0);
            using (var resetCursor = local.Command("SELECT downloaded_metadata_version FROM sync_publishers WHERE state='publishing'"))
                Assert.Equal(0L, resetCursor.ExecuteScalar());
            using (var restored = verified.Command("SELECT COUNT(*) FROM sync_video_content_identities"))
                Assert.Equal(1L, restored.ExecuteScalar());
            Assert.Equal(0, await sourcePublisher.PublishAsync(scope: scope));
            await sourcePublisher.PullMetadataChangesAsync(scope: scope);
            foreach (var useProposed in new[]{true,false})
            {
                localMetadata.SetConcept(1,"landscape","manual","rejected");
                var pending=Assert.Single(local.GetMetadataEdits());
                var winner=verified.GetMetadataEdits().Single(edit=>edit.Kind=="video-concept" && edit.Field.Contains("landscape",StringComparison.Ordinal));
                verified.AcceptMetadataEdits([winner with { OperationId=Guid.NewGuid().ToString("N"),ServerVersion=0,BaseVersion=winner.ServerVersion,Value="accepted" }]);
                await sourcePublisher.PublishAsync(scope:scope);
                await sourcePublisher.PullMetadataChangesAsync(scope:scope);
                Assert.Contains(local.GetMetadataEditConflicts(),item=>item.OperationId==pending.OperationId);
                using var list=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/metadata-conflicts",new { });
                list.EnsureSuccessStatusCode();
                var pendingSnapshot=(await list.Content.ReadFromJsonAsync<MetadataConflictSnapshot>(DeviceAuthorization.Json))!;
                var pendingReview=Assert.Single(pendingSnapshot.Reviews).Conflict;
                using var resolve=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/metadata-conflicts/resolve",
                    new MetadataConflictResolution(pendingReview.OperationId,useProposed,pendingReview.Current.ServerVersion,pendingReview.Current.OperationId,pendingSnapshot.Epoch));
                resolve.EnsureSuccessStatusCode();
                await sourcePublisher.PullMetadataChangesAsync(scope:scope);
                Assert.Empty(local.GetMetadataEditConflicts());
                Assert.Equal(useProposed?"rejected":"accepted",Assert.Single(local.GetMetadataEdits()).Value);
            }
            verified.UpsertMirroredPerson(new(100,"initial",0,0));
            var personEdit=new MetadataEdit("person",100,"name","current",DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N"));
            verified.AcceptMetadataEdits([personEdit]);
            verified.AcceptMetadataEdits([personEdit with { Value="proposed",OperationId=Guid.NewGuid().ToString("N") }]);
            using var conflictListing=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/metadata-conflicts",new { });
            conflictListing.EnsureSuccessStatusCode();
            var conflicts=(await conflictListing.Content.ReadFromJsonAsync<MetadataConflictSnapshot>(DeviceAuthorization.Json))!;
            var conflict=Assert.Single(conflicts.Reviews).Conflict;
            var resolution=new MetadataConflictResolution(conflict.OperationId,true,conflict.Current.ServerVersion,conflict.Current.OperationId,conflicts.Epoch);
            using var conflictDenied=await reader.SpaceClient.PostAsJsonAsync("api/sync/v2/metadata-conflicts/resolve",resolution);
            Assert.Equal(HttpStatusCode.Forbidden,conflictDenied.StatusCode);
            using var conflictStale=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/metadata-conflicts/resolve",resolution with { Epoch=Guid.NewGuid().ToString("N") });
            Assert.Equal(HttpStatusCode.Conflict,conflictStale.StatusCode);
            using var conflictApplied=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/metadata-conflicts/resolve",resolution);
            conflictApplied.EnsureSuccessStatusCode();
            using var conflictRetry=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/metadata-conflicts/resolve",resolution);
            conflictRetry.EnsureSuccessStatusCode();
            using var changedDecision=await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/metadata-conflicts/resolve",resolution with { UseProposed=false });
            Assert.Equal(HttpStatusCode.Conflict,changedDecision.StatusCode);
            Assert.Equal("proposed",new PeopleService(verified).Get(100)!.DisplayName);
            Assert.Empty(verified.GetMetadataEditConflicts());
            var remoteEdit = Assert.Single(verified.GetMetadataEdits().Where(edit => edit.Value == "accepted" && edit.Field.Contains("landscape", StringComparison.Ordinal)));
            using (var transaction = verified.BeginTransaction())
            {
                verified.QuarantineVideoMetadataOperation(remoteEdit.OperationId, transaction);
                transaction.Commit();
            }
            await sourcePublisher.PullMetadataChangesAsync(scope: scope);
            Assert.Empty(localMetadata.GetConcepts(1));
            Assert.DoesNotContain(local.GetMetadataEdits(), edit => edit.OperationId == localOperation);
            Assert.Single(local.GetVideoOwnershipReviews());
            // Approve the archived decision through the real authenticated API;
            // the next pull must receive its fresh authoritative operation.
            using var nextListing = await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/video-ownership-reviews", new { });
            nextListing.EnsureSuccessStatusCode();
            var nextReview = Assert.Single((await nextListing.Content.ReadFromJsonAsync<VideoOwnershipReview[]>(DeviceAuthorization.Json))!);
            var nextCandidate = Assert.Single(nextReview.Candidates);
            using var nextApplied = await editor.SpaceClient.PostAsJsonAsync("api/sync/v2/video-ownership-reviews/resolve",
                new VideoOwnershipResolution(nextReview.OperationId, [new(nextCandidate.VideoId, nextCandidate.Version, nextCandidate.CurrentOperationId)], Epoch: nextReview.Epoch));
            nextApplied.EnsureSuccessStatusCode();
            await sourcePublisher.PullMetadataChangesAsync(scope: scope);
            Assert.Contains("landscape", localMetadata.GetConcepts(1));
            Assert.NotEqual(localOperation, Assert.Single(local.GetMetadataEdits()).OperationId);
            var localClusters=new Picer.Core.Vision.ContentSemanticClusterService(local);
            localClusters.SaveSettings(false,0.76f);
            await sourcePublisher.PublishAsync(scope:scope);
            await sourcePublisher.PullMetadataChangesAsync(scope:scope);
            var settingsPath="api/service/v1/classification/clustering/settings";
            var settings=(await editor.SpaceClient.GetFromJsonAsync<Picer.Core.Vision.ContentClusterSettings>(settingsPath))!;
            Assert.False(settings.Automatic); Assert.Equal(0.76f,settings.ManualThreshold); Assert.True(settings.Version>0);
            using var required=await editor.SpaceClient.PostAsJsonAsync(settingsPath,new { Automatic=true,ManualThreshold=0.89f });
            Assert.Equal((HttpStatusCode)428,required.StatusCode);
            using var readerPreference=await reader.SpaceClient.PostAsJsonAsync(settingsPath,new { Automatic=true,ManualThreshold=0.89f,ExpectedVersion=settings.Version,ExpectedEpoch=settings.Epoch });
            Assert.Equal(HttpStatusCode.Forbidden,readerPreference.StatusCode);
            using var oldSpacePreference=await editor.SpaceClient.PostAsJsonAsync(settingsPath,new { Automatic=true,ManualThreshold=0.89f,ExpectedVersion=settings.Version,ExpectedEpoch=Guid.NewGuid().ToString("N") });
            Assert.Equal(HttpStatusCode.Conflict,oldSpacePreference.StatusCode);
            Assert.False(new Picer.Core.Vision.ContentSemanticClusterService(verified).GetSettings().Automatic);
            using var preferenceChanged=await editor.SpaceClient.PostAsJsonAsync(settingsPath,new { Automatic=true,ManualThreshold=0.89f,ExpectedVersion=settings.Version,ExpectedEpoch=settings.Epoch });
            preferenceChanged.EnsureSuccessStatusCode();
            await sourcePublisher.PullMetadataChangesAsync(scope:scope);
            Assert.True(localClusters.GetSettings().Automatic); Assert.Equal(0.89f,localClusters.GetSettings().ManualThreshold);
            using var stalePreference=await editor.SpaceClient.PostAsJsonAsync(settingsPath,new { Automatic=false,ManualThreshold=0.92f,ExpectedVersion=settings.Version,ExpectedEpoch=settings.Epoch });
            Assert.Equal(HttpStatusCode.Conflict,stalePreference.StatusCode);
            Assert.True(new Picer.Core.Vision.ContentSemanticClusterService(verified).GetSettings().Automatic);
            var concurrentBaseline=(await editor.SpaceClient.GetFromJsonAsync<Picer.Core.Vision.ContentClusterSettings>(settingsPath))!;
            var competing=await Task.WhenAll(new[]{0.73f,0.94f}.Select(threshold=>editor.SpaceClient.PostAsJsonAsync(settingsPath,
                new { Automatic=false,ManualThreshold=threshold,ExpectedVersion=concurrentBaseline.Version,ExpectedEpoch=concurrentBaseline.Epoch })));
            try
            {
                Assert.Single(competing,response=>response.StatusCode==HttpStatusCode.OK);
                Assert.Single(competing,response=>response.StatusCode==HttpStatusCode.Conflict);
            }
            finally { foreach (var response in competing) response.Dispose(); }
            if (!verifiedVideoHash)
            {
                using (var model=verified.Command("""
                    INSERT INTO semantic_content_clusters(cluster_key,model_version,display_name,confidence,member_count,created_utc,updated_utc)
                    VALUES('model-isolation-test','remote-model','动态分类',0.9,0,$now,$now)
                    """))
                { model.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));model.ExecuteNonQuery(); }
                var modelSettings=(await editor.SpaceClient.GetFromJsonAsync<Picer.Core.Vision.ContentClusterSettings>(settingsPath))!;
                Assert.Equal("remote-model",modelSettings.ModelVersion);Assert.True(modelSettings.Automatic);
                using var changedModel=await editor.SpaceClient.PostAsJsonAsync(settingsPath,new { Automatic=false,ManualThreshold=0.80f,
                    ExpectedVersion=modelSettings.Version,ExpectedEpoch=modelSettings.Epoch });
                Assert.Equal(HttpStatusCode.Conflict,changedModel.StatusCode);
                using var modelSaved=await editor.SpaceClient.PostAsJsonAsync(settingsPath,new { Automatic=false,ManualThreshold=0.80f,
                    ExpectedVersion=modelSettings.Version,ExpectedEpoch=modelSettings.Epoch,ModelVersion=modelSettings.ModelVersion });
                modelSaved.EnsureSuccessStatusCode();
                using(var stillPresent=verified.Command("SELECT COUNT(*) FROM semantic_content_clusters WHERE cluster_key='model-isolation-test'"))
                    Assert.Equal(1L,stillPresent.ExecuteScalar()); // The service has no vectors: never erase uploaded clusters.
                await sourcePublisher.PullMetadataChangesAsync(scope:scope);
                Assert.Equal(0.80f,localClusters.GetSettingsForModel("remote-model").EffectiveThreshold);
            }
            // Video face decisions use the same authenticated, paged publisher
            // channel, with distinct local and service IDs and resumable scope.
            local.UpsertMirroredPerson(new(1,"来源视频人物甲",0,0));local.UpsertMirroredPerson(new(2,"来源视频人物乙",0,0));
            var faceTime=DateTime.UtcNow.ToString("O");
            local.UpsertMirroredVideoMetadata(new Picer.Core.Gateway.GatewayMetadata([],[],[]) with
            {
                VideoKeyframes=[new(5000,1,0,"",0)],
                VideoFaces=[new(6000,1,5000,0,0,0,0,10,10,0.9,null,null,null,1,"confirmed",faceTime,faceTime) { ModelVersion="source-face-model" }]
            });
            using(var transaction=local.BeginTransaction())
            {
                local.RecordVideoFaceManualState(6000,transaction);
                local.RecordVideoFacePersonRejection(6000,2,true,transaction);
                local.ReplayMetadataEditsInTransaction(transaction);transaction.Commit();
            }
            var peopleScope=scope with { People=true };
            await sourcePublisher.PublishAsync(scope:peopleScope);await sourcePublisher.PullMetadataChangesAsync(scope:peopleScope);
            Assert.True(local.GetMetadataEdits().Single(edit=>edit.Kind=="video-face-state").ServerVersion>0);
            long remoteFace;
            using(var mapped=verified.Command("SELECT target_id FROM sync_entity_identities WHERE kind='video-face' AND local_key='6000'"))
                remoteFace=Convert.ToInt64(mapped.ExecuteScalar());
            Assert.NotEqual(6000,remoteFace);
            var stateField=verified.GetMetadataEdits().Single(edit=>edit.Kind=="video-face-state"&&edit.EntityId==remoteFace);
            var rejectionField=verified.GetMetadataEdits().Single(edit=>edit.Kind=="video-face-rejection"&&edit.EntityId==remoteFace);
            verified.AcceptMetadataEdits([
                stateField with { Value=JsonSerializer.Serialize(new FaceManualState(null,"ignored","source-face-model")),
                    BaseVersion=stateField.ServerVersion,ServerVersion=0,OperationId=Guid.NewGuid().ToString("N") },
                rejectionField with { Value=JsonSerializer.Serialize(new FacePersonRejection(false,"source-face-model")),
                    BaseVersion=rejectionField.ServerVersion,ServerVersion=0,OperationId=Guid.NewGuid().ToString("N") }]);
            await sourcePublisher.PullMetadataChangesAsync(scope:scope);
            Assert.Equal("confirmed",local.GetAllMirroredVideoFaces().Single(face=>face.Id==6000).AssignmentState);
            await sourcePublisher.PullMetadataChangesAsync(scope:peopleScope);
            Assert.Equal("ignored",local.GetAllMirroredVideoFaces().Single(face=>face.Id==6000).AssignmentState);
            var downloaded=local.GetMetadataEdits().Single(edit=>edit.Kind=="video-face-rejection");
            Assert.Equal("2",downloaded.Field);Assert.False(PicerDatabase.ParseFacePersonRejection(downloaded.Value).Rejected);
            using(var rejects=local.Command("SELECT COUNT(*) FROM video_face_person_rejections WHERE video_face_id=6000")) Assert.Equal(0L,rejects.ExecuteScalar());
            Assert.Equal(0,await sourcePublisher.PublishAsync(scope:peopleScope));
        }
        finally
        {
            await instance.App.StopAsync(); await instance.App.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root,true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task LostCommittedResponseResumesDurableBatchAndPublishesDescriptionsAndCatalog(bool restrictScope, bool corruptBaselineReceipt)
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-publisher-" + Guid.NewGuid().ToString("N"));
        const string origin = "https://picer.test";
        var instance = AccountServiceHost.Build(Path.Combine(root, "service"), origin, 0);
        try
        {
            var address = await IndexServiceHost.StartAsync(instance.App);
            var owner = instance.Accounts.Register("publisher-user", "test-publisher-password!", "照片空间");
            var space = Assert.Single(instance.Accounts.ListSpaces(owner));
            using var signing = IdentitySigningKey.LoadOrCreate(instance.Accounts.Root, instance.Accounts.ServiceId);
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = DeviceAuthorization.ExportPublicKey(key);
            var request = Guid.NewGuid().ToString("N");
            instance.Accounts.CreateEnrollment(new(request, publicKey, "电脑 A", "gui"));
            var pin = Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            var document = instance.Accounts.ApproveEnrollment(owner, request, space.Id, origin, pin, signing, true, false);
            var connection = new DeviceConnection(origin, instance.Accounts.ServiceId, pin, DeviceAuthorization.ExportPublicKey(signing), "test", request, "电脑 A", "gui");
            var bridge = new AddressBridge(address, DeviceAccountClient.CreateTransport(pin));
            using var client = new DeviceAccountClient(connection, key, value => connection = value, bridge);
            await client.ImportAsync(DeviceAuthorization.WriteFileDocument(document));
            Assert.NotNull(connection.Session);
            using var local = new PicerDatabase(Path.Combine(root, "local", "picer.db"));
            var source = local.AddSource(@"C:\Photos");
            var excluded = local.AddSource(@"D:\Private");
            local.UpsertMirroredImage(new ImageRecord { Id = 2, SourceId = excluded.Id, Path = @"D:\Private\private.jpg", FileSize = 101,
                ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Sha256 = new string('d', 64), ContentSha256 = new string('d', 64) });
            local.UpsertMirroredImage(new ImageRecord { Id = 1, SourceId = source.Id, Path = @"C:\Photos\one.jpg", FileSize = 100,
                ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Gps = "31.2,121.5", Sha256 = new string('c', 64), ContentSha256 = new string('c', 64) });
            local.UpsertMirroredPerson(new PersonSummary(1, "家人", 0, 0));
            local.UpsertMirroredFaces([new(1, 1, "model-v1", 0, 0, 0, 10, 10, 0.99, null, null, DateTime.UtcNow.ToString("O"))],
                [new(1, 1, "confirmed", 0.99, "user", DateTime.UtcNow.ToString("O"))]);
            new PeopleService(local).ConfirmFace(1,1);
            local.UpsertMirroredTag(new Tag { Id = 1, Name = "旅行", CreatedUtc = DateTime.UtcNow });
            local.UpsertMirroredImageTag(1, 1);
            local.UpsertMirroredVideoMetadata(new([], [], [], [], [], [], [], [], CloudVisionAnalyses:
                [new(1, "test", "test-model", "test-prompt", "completed", "海边旅行的照片", "{}", DateTime.UtcNow.ToString("O"), 100, DateTime.UtcNow.ToString("O"))]));
            var classificationTime = DateTime.UtcNow.ToString("O");
            using (var classification = local.Command("""
                INSERT INTO image_classification_assertions(image_id,content_sha256,concept_id,source_kind,source_version,
                    assertion_state,review_state,confidence,evidence_fingerprint,created_utc,updated_utc)
                VALUES(1,$sha,'scene.beach','vision_model','clip-v1','automatic','accepted',0.97,'clip-v1',$now,$now);
                INSERT INTO image_classification_assertions(image_id,content_sha256,concept_id,source_kind,source_version,
                    assertion_state,review_state,confidence,evidence_fingerprint,created_utc,updated_utc)
                VALUES(2,$privateSha,'scene.private','vision_model','clip-v1','automatic','accepted',0.99,'clip-v1',$now,$now);
                INSERT INTO image_classification_rejections(image_id,concept_id,source_scope,rejected_utc)
                VALUES(1,'scene.snow','vision_model',$now);
                INSERT INTO semantic_content_clusters(cluster_key,model_version,display_name,confidence,member_count,created_utc,updated_utc)
                VALUES('cluster-public','clip-v1','海边旅行',0.97,1,$now,$now),('cluster-private','clip-v1','私人',0.99,1,$now,$now);
                INSERT INTO semantic_content_cluster_members(cluster_key,image_id,similarity,semantic_signature)
                VALUES('cluster-public',1,0.97,'public-signature'),('cluster-private',2,0.99,'private-signature');
                """))
            {
                classification.Parameters.AddWithValue("$sha", new string('c', 64));
                classification.Parameters.AddWithValue("$privateSha", new string('d', 64));
                classification.Parameters.AddWithValue("$now", classificationTime);
                classification.ExecuteNonQuery();
            }
            var publisher = new SpaceSyncPublisher(client, local, Path.Combine(root, "snapshots"));
            var scope = new SpaceUploadScope([source.Id], People: !restrictScope, PreciseLocation: !restrictScope);
            bridge.FailNextCommittedBatch = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => publisher.PublishAsync(scope: scope));
            using (var pending = local.Command("SELECT COUNT(*) FROM sync_outbox")) Assert.Equal(1L, pending.ExecuteScalar());
            if (!restrictScope && !corruptBaselineReceipt)
            {
                using var previousExport = local.Command("UPDATE sync_exports SET metadata_protocol=2");
                previousExport.ExecuteNonQuery();
            }
            Assert.True(await publisher.PublishAsync(scope: scope) >= 3);
            using (var protocol = local.Command("SELECT metadata_protocol FROM sync_publishers WHERE state='publishing'"))
                Assert.Equal((long)SpaceSyncProtocol.MetadataVersion, protocol.ExecuteScalar());
            using (var pending = local.Command("SELECT COUNT(*) FROM sync_outbox")) Assert.Equal(0L, pending.ExecuteScalar());
            using (var exported = local.Command("SELECT COUNT(*) FROM sync_exports")) Assert.Equal(0L, exported.ExecuteScalar());
            using var remote = new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id), "picer.db"));
            var remoteImage = Assert.Single(remote.GetImages(includeMissing: true));
            Assert.Equal(restrictScope ? null : "31.2,121.5", remoteImage.Gps);
            Assert.Equal(restrictScope ? 0 : 1, new PeopleService(remote).GetAll().Count);
            Assert.Equal(restrictScope ? 0 : 1, remote.GetAllMirroredFaces().Count);
            Assert.Equal(restrictScope?0:1,remote.GetMetadataEdits().Count(edit=>edit.Kind=="face-state"));
            Assert.Equal("旅行", Assert.Single(SemanticEngine.GetImageTags(remote, remoteImage.Id)).Name);
            Assert.Equal("海边旅行的照片", Assert.Single(remote.GetAllMirroredCloudVisionAnalyses()).Summary);
            using (var selectedClasses = remote.Command("SELECT COUNT(*) FROM image_classification_assertions WHERE concept_id='scene.beach'"))
                Assert.Equal(1L, selectedClasses.ExecuteScalar());
            using (var excludedClasses = remote.Command("SELECT COUNT(*) FROM image_classification_assertions WHERE concept_id='scene.private'"))
                Assert.Equal(0L, excludedClasses.ExecuteScalar());
            using (var selectedRejections = remote.Command("SELECT COUNT(*) FROM image_classification_rejections WHERE concept_id='scene.snow'"))
                Assert.Equal(1L, selectedRejections.ExecuteScalar());
            using (var clusters = remote.Command("SELECT COUNT(*) FROM semantic_content_clusters")) Assert.Equal(1L, clusters.ExecuteScalar());
            using (var clusterMembers = remote.Command("SELECT COUNT(*) FROM semantic_content_cluster_members")) Assert.Equal(1L, clusterMembers.ExecuteScalar());
            Assert.Equal(0, await publisher.PublishAsync(scope: scope));
            Assert.Single(remote.GetImages(includeMissing: true));
            Assert.Single(remote.GetAllMirroredCloudVisionAnalyses());
            Assert.Equal(2, local.GetImages(includeMissing: true).Count());
            Assert.Equal(2, local.GetSources().Count());
            Assert.Equal("31.2,121.5", local.GetImage(1)!.Gps);
            Assert.Single(new PeopleService(local).GetAll());
            Assert.Single(local.GetAllMirroredFaces());
            using (var remoteEdit = remote.Command("""
                UPDATE cloud_vision_analyses SET summary='远程更正后的描述',updated_utc=$updated WHERE image_id=$image;
                UPDATE image_classification_assertions SET review_state='rejected',updated_utc=$updated WHERE image_id=$image;
                """))
            {
                remoteEdit.Parameters.AddWithValue("$image", remoteImage.Id);
                remoteEdit.Parameters.AddWithValue("$updated", DateTime.UtcNow.AddMinutes(1).ToString("O"));
                remoteEdit.ExecuteNonQuery();
            }
            Assert.True(await publisher.PullMetadataChangesAsync(scope: scope) > 0);
            Assert.Equal("远程更正后的描述", Assert.Single(local.GetAllMirroredCloudVisionAnalyses()).Summary);
            using (var decision = local.Command("SELECT review_state FROM image_classification_assertions WHERE image_id=1 AND concept_id='scene.beach'"))
                Assert.Equal("rejected", decision.ExecuteScalar());
            using (var untouched = local.Command("SELECT COUNT(*) FROM image_classification_assertions WHERE image_id=2 AND concept_id='scene.private'"))
                Assert.Equal(1L, untouched.ExecuteScalar());
            using (var clusterKey = local.Command("SELECT cluster_key FROM semantic_content_cluster_members WHERE image_id=1"))
                Assert.Equal("cluster-public", clusterKey.ExecuteScalar());
            Assert.Single(local.GetAllMirroredFaces()); // Private local people survive a restricted upload.
            Assert.Equal(0, await publisher.PublishAsync(scope: scope)); // Downlink does not become another full upload.
            if (!restrictScope && !corruptBaselineReceipt)
            {
                var person=new PeopleService(remote).GetAll().Single().Id;
                using (var transaction=remote.BeginTransaction())
                {
                    for (var index=0;index<650;index++)
                    {
                        var alias="分页恢复"+index;
                        remote.RecordMetadataEdit("person",person,PicerDatabase.AliasField(alias),alias,transaction);
                    }
                    remote.ReplayMetadataEditsInTransaction(transaction); transaction.Commit();
                }
                var pageStart=bridge.MetadataPageAfters.Count; bridge.MetadataPagesBeforeDisconnect=2;
                await Assert.ThrowsAsync<HttpRequestException>(()=>publisher.PullMetadataChangesAsync(scope:scope));
                using (var count=local.Command("SELECT COUNT(*) FROM person_aliases WHERE person_id=1 AND alias LIKE '分页恢复%'")) Assert.Equal(500L,count.ExecuteScalar());
                long committed;
                using (var cursor=local.Command("SELECT downloaded_metadata_version FROM sync_publishers")) committed=Convert.ToInt64(cursor.ExecuteScalar());
                Assert.True(committed>0);
                publisher=new SpaceSyncPublisher(client,local,Path.Combine(root,"snapshots"));
                await publisher.PullMetadataChangesAsync(scope:scope);
                Assert.Equal(committed,bridge.MetadataPageAfters[pageStart+2]);
                using (var count=local.Command("SELECT COUNT(*) FROM person_aliases WHERE person_id=1 AND alias LIKE '分页恢复%'")) Assert.Equal(650L,count.ExecuteScalar());
                var narrowed=scope with { People=false };
                await publisher.PullMetadataChangesAsync(scope:narrowed);
                using (var transaction=remote.BeginTransaction())
                {
                    remote.RecordMetadataEdit("person",person,"description","恢复范围后接收",transaction);
                    remote.ReplayMetadataEditsInTransaction(transaction); transaction.Commit();
                }
                await publisher.PullMetadataChangesAsync(scope:narrowed);
                using (var description=local.Command("SELECT description FROM people WHERE id=1")) Assert.NotEqual("恢复范围后接收",description.ExecuteScalar());
                await publisher.PullMetadataChangesAsync(scope:scope);
                using (var description=local.Command("SELECT description FROM people WHERE id=1")) Assert.Equal("恢复范围后接收",description.ExecuteScalar());
                Assert.Equal(0,await publisher.PublishAsync(scope:scope));
                var remoteFace=Assert.Single(remote.GetAllMirroredFaces()).Id;
                new PeopleService(remote).IgnoreFace(remoteFace);
                await publisher.PullMetadataChangesAsync(scope:narrowed);
                Assert.Single(local.GetAllMirroredFaceAssignments());
                await publisher.PullMetadataChangesAsync(scope:scope);
                Assert.Empty(local.GetAllMirroredFaceAssignments());
                Assert.Equal("ignored",Assert.Single(local.GetFaceReviewDecisions()).State);
                new PeopleService(local).MoveFaceToUnassigned(1);
                await publisher.PublishAsync(scope:scope);
                await publisher.PullMetadataChangesAsync(scope:scope);
                Assert.Equal("active",Assert.Single(remote.GetFaceReviewDecisions()).State);
                Assert.Empty(remote.GetAllMirroredFaceAssignments());
                Assert.Equal(0,await publisher.PublishAsync(scope:scope));
                // A page may have advanced while a new person had no local identity.
                // Confirming its later adoption must replay the skipped manual field.
                var addedPerson=new PeopleService(remote).Create("远程认领测试人物");
                new PeopleService(remote).ConfirmFace(remoteFace,addedPerson.Id);
                remote.PromoteUnversionedMetadata();
                var faceVersion=remote.GetMetadataEdits().Single(edit=>edit.Kind=="face-state").ServerVersion;
                using(var skipped=local.Command("UPDATE sync_publishers SET downloaded_metadata_version=$version,downloaded_metadata_operation='~'"))
                { skipped.Parameters.AddWithValue("$version",faceVersion);skipped.ExecuteNonQuery(); }
                await publisher.PullMetadataChangesAsync(scope:scope);
                var adoptedPerson=new PeopleService(local).GetAll().Single(item=>item.DisplayName=="远程认领测试人物");
                Assert.Equal(adoptedPerson.Id,Assert.Single(local.GetAllMirroredFaceAssignments()).PersonId);
                Assert.Equal(0,await publisher.PublishAsync(scope:scope));
                new PeopleService(remote).MarkNotThisPerson(remoteFace,addedPerson.Id);
                await publisher.PullMetadataChangesAsync(scope:scope);
                Assert.False(new PeopleService(local).SuggestFace(1,adoptedPerson.Id,0.99));
                new PeopleService(local).ConfirmFace(1,adoptedPerson.Id);
                await publisher.PublishAsync(scope:scope);await publisher.PullMetadataChangesAsync(scope:scope);
                using(var rejected=remote.Command("SELECT COUNT(*) FROM face_person_rejections WHERE face_id=$face AND person_id=$person"))
                { rejected.Parameters.AddWithValue("$face",remoteFace);rejected.Parameters.AddWithValue("$person",addedPerson.Id);Assert.Equal(0L,rejected.ExecuteScalar()); }
                Assert.Equal(0,await publisher.PublishAsync(scope:scope));
            }
            using (var change = local.Command("UPDATE images SET file_size=file_size+1 WHERE id=1")) change.ExecuteNonQuery();
            Assert.Equal(1, await publisher.PublishAsync(scope: scope));
            Assert.Equal(101, Assert.Single(remote.GetImages(includeMissing: true)).FileSize);
            Assert.Single(remote.GetAllMirroredCloudVisionAnalyses());
            Assert.Equal(0, await publisher.PublishAsync(scope: scope));
            using (var remoteDelete = remote.Command("DELETE FROM cloud_vision_analyses WHERE image_id=$image"))
            { remoteDelete.Parameters.AddWithValue("$image", remoteImage.Id); remoteDelete.ExecuteNonQuery(); }
            Assert.True(await publisher.PullMetadataChangesAsync(scope: scope) > 0);
            Assert.Empty(local.GetAllMirroredCloudVisionAnalyses());
            var remoteTag = SemanticEngine.AddTag(remote, "远程新增标签");
            SemanticEngine.AssignTag(remote, remoteImage.Id, remoteTag.Id);
            var remoteAlbum = SemanticEngine.AddAlbum(remote, "远程新增相册", "remote description");
            SemanticEngine.AddToAlbum(remote, remoteAlbum.Id, remoteImage.Id);
            bridge.FailNextCommittedAdoption = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => publisher.PullMetadataChangesAsync(scope: scope));
            using (var pending = local.Command("SELECT COUNT(*) FROM sync_pending_identity_adoptions")) Assert.Equal(2L, pending.ExecuteScalar());
            publisher = new SpaceSyncPublisher(client, local, Path.Combine(root, "snapshots"));
            if (corruptBaselineReceipt)
            {
                await publisher.PublishAsync(scope: scope with { Photos = false, Videos = true, People = false });
                using (var deferred = local.Command("SELECT COUNT(*) FROM sync_pending_identity_adoptions")) Assert.Equal(2L, deferred.ExecuteScalar());
                Assert.Single(SemanticEngine.GetTags(remote).Where(tag => tag.Name == "远程新增标签"));
                await publisher.PublishAsync(scope: scope); // Restoring scope resumes the same identities.
            }
            else Assert.Equal(0, await publisher.PublishAsync(scope: scope)); // Confirm before any possible re-export.
            using (var pending = local.Command("SELECT COUNT(*) FROM sync_pending_identity_adoptions")) Assert.Equal(0L, pending.ExecuteScalar());
            await publisher.PullMetadataChangesAsync(scope: scope);
            Assert.Contains(SemanticEngine.GetImageTags(local, 1), tag => tag.Name == "远程新增标签");
            Assert.Single(SemanticEngine.GetAlbums(local).Where(album => album.Name == "远程新增相册"));
            Assert.Equal(0, await publisher.PublishAsync(scope: scope));
            Assert.Single(SemanticEngine.GetTags(remote).Where(tag => tag.Name == "远程新增标签"));
            Assert.Single(SemanticEngine.GetAlbums(remote).Where(album => album.Name == "远程新增相册"));
            using var gateway = new Picer.Core.Gateway.HttpIndexGateway(client.SpaceClient);
            if (corruptBaselineReceipt)
            {
                local.UpsertMirroredImage(new ImageRecord { Id = 3, SourceId = source.Id, Path = @"C:\Photos\temporary.jpg",
                    FileSize = 10, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow,
                    Sha256 = new string('e', 64), ContentSha256 = new string('e', 64) });
                await publisher.PublishAsync(scope: scope);
                var temporary = remote.GetImages(includeMissing: true).Single(image => image.Sha256 == new string('e', 64));
                var pendingTag = SemanticEngine.AddTag(remote, "基线切换中认领的标签");
                SemanticEngine.AssignTag(remote, remoteImage.Id, pendingTag.Id);
                bridge.FailBeforeNextAdoption = true;
                await Assert.ThrowsAsync<HttpRequestException>(() => publisher.PullMetadataChangesAsync(scope: scope));
                using (var pending = local.Command("SELECT COUNT(*) FROM sync_pending_identity_adoptions")) Assert.Equal(1L, pending.ExecuteScalar());
                using (var missing = remote.Command("UPDATE images SET status='missing' WHERE id=$id"))
                { missing.Parameters.AddWithValue("$id", temporary.Id); missing.ExecuteNonQuery(); }
                await gateway.GetChangesAsync(new());
                Assert.True(await gateway.PurgeMissingImageAsync(temporary.Id));
                publisher = new SpaceSyncPublisher(client, local, Path.Combine(root, "snapshots"));
                await publisher.PublishAsync(scope: scope);
                using (var pending = local.Command("SELECT COUNT(*) FROM sync_pending_identity_adoptions")) Assert.Equal(0L, pending.ExecuteScalar());
                Assert.Null(local.GetImage(3));
                await publisher.PullMetadataChangesAsync(scope: scope);
                Assert.Single(SemanticEngine.GetImageTags(local, 1).Where(tag => tag.Name == "基线切换中认领的标签"));
                Assert.Single(SemanticEngine.GetTags(remote).Where(tag => tag.Name == "基线切换中认领的标签"));
            }
            // An owner purge invalidates old publications. The next upload reconciles
            // against remaining identities instead of resurrecting its old local copy.
            using (var markMissing = remote.Command("UPDATE images SET status='missing' WHERE id=$id"))
            { markMissing.Parameters.AddWithValue("$id", remoteImage.Id); markMissing.ExecuteNonQuery(); }
            await gateway.GetChangesAsync(new());
            Assert.True(await gateway.PurgeMissingImageAsync(remoteImage.Id));
            Assert.Empty(remote.GetImages(includeMissing: true));
            bridge.FailNextCommittedRebaseline = !corruptBaselineReceipt;
            bridge.CorruptNextRebaselineReceipt = corruptBaselineReceipt;
            if (corruptBaselineReceipt)
                await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishAsync(scope: scope));
            else
                await Assert.ThrowsAsync<HttpRequestException>(() => publisher.PublishAsync(scope: scope));
            using (var recovering = local.Command("SELECT state FROM sync_publishers"))
                Assert.Equal("rebaselining", recovering.ExecuteScalar());
            publisher = new SpaceSyncPublisher(client, local, Path.Combine(root, "snapshots"));
            await publisher.PublishAsync(scope: scope);
            using (var recovered = local.Command("SELECT state FROM sync_publishers"))
                Assert.Equal("publishing", recovered.ExecuteScalar());
            Assert.Null(local.GetImage(1));
            Assert.DoesNotContain(local.GetMetadataEdits(),edit=>edit.Kind=="face-state");
            using(var purgedMarkers=local.Command("SELECT COUNT(*) FROM sync_imported_metadata_edits WHERE entity_kind='face-state'"))
                Assert.Equal(0L,purgedMarkers.ExecuteScalar());
            Assert.NotNull(local.GetImage(2)); // The unselected local source is untouched.
            Assert.Empty(remote.GetImages(includeMissing: true));
            Assert.Equal(0, await publisher.PublishAsync(scope: scope));
        }
        finally
        {
            await instance.App.StopAsync(); await instance.App.DisposeAsync(); instance.Certificate.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class AddressBridge(string address, HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        public Action<SpaceSyncBatch>? AfterCommittedBatch { get; set; }
        public string? FailAfterCommittedCatalogPath { get; set; }
        public Action<string>? AfterCommittedCatalog { get; set; }
        public bool FailNextCommittedPurge { get; set; }
        public bool FailNextCommittedBatch { get; set; }
        public bool FailNextCommittedAdoption { get; set; }
        public bool FailNextCommittedRebaseline { get; set; }
        public bool CorruptNextRebaselineReceipt { get; set; }
        public bool FailBeforeNextAdoption { get; set; }
        public int MetadataPagesBeforeDisconnect { get; set; }
        public List<long> MetadataPageAfters { get; }=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isBaselineCompletion = request.RequestUri!.AbsolutePath.EndsWith("/api/sync/v2/rebaseline", StringComparison.Ordinal) &&
                request.Content is not null &&
                System.Text.Json.JsonSerializer.Deserialize<SpaceRebaselineRequest>(
                    await request.Content.ReadAsStringAsync(cancellationToken), DeviceAuthorization.Json)?.Complete == true;
            request.Headers.Host = "picer.test";
            request.RequestUri = new Uri(address + request.RequestUri!.PathAndQuery);
            if (FailBeforeNextAdoption && request.RequestUri.AbsolutePath.EndsWith("/api/sync/v2/identity-adoptions", StringComparison.Ordinal))
            {
                FailBeforeNextAdoption = false;
                throw new HttpRequestException("simulated_disconnect_before_identity_adoption_commit");
            }
            var response = await base.SendAsync(request, cancellationToken);
            if(FailNextCommittedPurge && response.IsSuccessStatusCode &&
                (request.RequestUri.AbsolutePath.EndsWith("/purge",StringComparison.Ordinal) || request.RequestUri.AbsolutePath.EndsWith("/purge-missing",StringComparison.Ordinal)))
            {
                FailNextCommittedPurge=false;response.Dispose();throw new HttpRequestException("simulated_disconnect_after_purge_commit");
            }
            if(response.IsSuccessStatusCode && request.RequestUri.AbsolutePath.Contains("/catalog-retirements/",StringComparison.Ordinal))
                AfterCommittedCatalog?.Invoke(request.RequestUri.AbsolutePath);
            if(response.IsSuccessStatusCode && FailAfterCommittedCatalogPath is { } failurePath
                && request.RequestUri.AbsolutePath.EndsWith(failurePath,StringComparison.Ordinal))
            {
                FailAfterCommittedCatalogPath=null;response.Dispose();
                throw new HttpRequestException("simulated_disconnect_after_catalog_commit");
            }
            if (AfterCommittedBatch is not null && response.IsSuccessStatusCode && request.Content is not null &&
                request.RequestUri.AbsolutePath.EndsWith("/api/sync/v2/batches",StringComparison.Ordinal))
                AfterCommittedBatch(JsonSerializer.Deserialize<SpaceSyncBatch>(await request.Content.ReadAsStringAsync(cancellationToken),DeviceAuthorization.Json)!);
            if (request.RequestUri.AbsolutePath.EndsWith("/api/sync/v2/metadata-edit-page",StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                var after=request.RequestUri.Query.TrimStart('?').Split('&').Single(value=>value.StartsWith("after=",StringComparison.Ordinal));
                MetadataPageAfters.Add(long.Parse(after[6..],System.Globalization.CultureInfo.InvariantCulture));
                if (MetadataPagesBeforeDisconnect>0 && --MetadataPagesBeforeDisconnect==0)
                {
                    response.Dispose(); throw new HttpRequestException("simulated_disconnect_before_metadata_page_commit");
                }
            }
            if (CorruptNextRebaselineReceipt && isBaselineCompletion && response.IsSuccessStatusCode)
            {
                CorruptNextRebaselineReceipt = false;
                var payload = System.Text.Json.JsonSerializer.Deserialize<SpaceRebaselinePage>(
                    await response.Content.ReadAsStringAsync(cancellationToken), DeviceAuthorization.Json)!;
                response.Content.Dispose();
                response.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(
                    payload with { Publisher = payload.Publisher with { Epoch = Guid.NewGuid().ToString("N") } },
                    DeviceAuthorization.Json), System.Text.Encoding.UTF8, "application/json");
            }
            if (FailNextCommittedRebaseline && isBaselineCompletion && response.IsSuccessStatusCode)
            {
                FailNextCommittedRebaseline = false;
                response.Dispose();
                throw new HttpRequestException("simulated_disconnect_after_rebaseline_commit");
            }
            if (FailNextCommittedAdoption && request.RequestUri.AbsolutePath.EndsWith("/api/sync/v2/identity-adoptions", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                FailNextCommittedAdoption = false;
                response.Dispose();
                throw new HttpRequestException("simulated_disconnect_after_identity_adoption_commit");
            }
            if (FailNextCommittedBatch && request.RequestUri.AbsolutePath.EndsWith("/api/sync/v2/batches", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                FailNextCommittedBatch = false;
                response.Dispose();
                throw new HttpRequestException("simulated_disconnect_after_commit");
            }
            return response;
        }
    }
}
