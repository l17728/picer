using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.Core.Vision;
using Picer.IndexService;
using Picer.IndexService.Identity;

namespace Picer.Tests;

public sealed partial class SpacePublisherIntegrationTests
{
    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(false,true)]
    public async Task SceneDescriptionsRoundTripWithRemoteEditingAndConflictProtection(bool remoteCreation,bool concurrent)
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-scene-api-"+Guid.NewGuid().ToString("N"));
        const string origin="https://picer.test";
        var instance=AccountServiceHost.Build(Path.Combine(root,"service"),origin,0);
        try
        {
            var address=await IndexServiceHost.StartAsync(instance.App);
            var owner=instance.Accounts.Register("derived-user","test-derived-password!","模型同步测试");
            var space=Assert.Single(instance.Accounts.ListSpaces(owner));
            using var signing=IdentitySigningKey.LoadOrCreate(instance.Accounts.Root,instance.Accounts.ServiceId);
            var pin=Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var request=Guid.NewGuid().ToString("N");
            instance.Accounts.CreateEnrollment(new(request,DeviceAuthorization.ExportPublicKey(key),"模型同步设备","gui"));
            var document=instance.Accounts.ApproveEnrollment(owner,request,space.Id,origin,pin,signing,true,false);
            var connection=new DeviceConnection(origin,instance.Accounts.ServiceId,pin,DeviceAuthorization.ExportPublicKey(signing),"test",request,"模型同步设备","gui");
            var bridge=new AddressBridge(address,DeviceAccountClient.CreateTransport(pin));
            using var client=new DeviceAccountClient(connection,key,value=>connection=value,bridge);
            await client.ImportAsync(DeviceAuthorization.WriteFileDocument(document));
            using var local=new PicerDatabase(Path.Combine(root,"local","picer.db"));
            var source=local.AddSource(@"C:\Photos");

            local.UpsertMirroredImage(new ImageRecord {Id=7000,SourceId=source.Id,Path=@"C:\Photos\one.jpg",FileSize=100,
                ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow,Status=ImageStatus.Active});
            var scenes=new SceneGroupDescriptionService(local,new CloudVisionRepository(local));
            var plan=scenes.BuildPlan(source.Path,[local.GetImage(7000)!]);
            if(!remoteCreation)scenes.SaveAccepted(plan,new(plan.Fingerprint,"原场景","原描述",["原场景"],"user","manual-edit","manual-edit-v1","accepted"));
            var publisher=new SpaceSyncPublisher(client,local,Path.Combine(root,"snapshots"));var scope=new SpaceUploadScope([source.Id],People:true,PreciseLocation:true);
            await publisher.PublishAsync(scope:scope);await publisher.PullMetadataChangesAsync(scope:scope);
            using var remote=new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id),"picer.db"));
            var image=Assert.Single(remote.GetImages());
            var scene=remote.GetAllMirroredSceneGroupDescriptions().SingleOrDefault();
            var expected=scene is null?null:new SceneDescriptionText(scene.SceneName,scene.Summary,JsonSerializer.Deserialize<string[]>(scene.SearchTermsJson)!,scene.Provider,scene.ModelVersion,scene.PromptVersion);
            using var epochQuery=remote.Command("SELECT epoch FROM mirror_revision WHERE id=1");var epoch=(string)epochQuery.ExecuteScalar()!;
            var path=image.Path[..image.Path.LastIndexOf('\\')];
            var mutation=new SceneDescriptionMutation(Guid.NewGuid().ToString("N"),scene?.Fingerprint,[image.Id],path,
                new("远程场景","远程描述",["远程场景"],"user","manual-edit","manual-edit-v1"),expected,epoch);
            using(var result=await client.SpaceClient.PostAsJsonAsync("api/service/v1/scenes/edit",mutation))Assert.Equal(HttpStatusCode.OK,result.StatusCode);
            using(var replay=await client.SpaceClient.PostAsJsonAsync("api/service/v1/scenes/edit",mutation))Assert.Equal(HttpStatusCode.OK,replay.StatusCode);
            Assert.Single(remote.GetAllMirroredSceneGroupDescriptions());
            using(var stale=await client.SpaceClient.PostAsJsonAsync("api/service/v1/scenes/edit",mutation with {Text=mutation.Text with {SceneName="Stale modification"}}))Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
            using(var wrongEpoch=await client.SpaceClient.PostAsJsonAsync("api/service/v1/scenes/edit",mutation with {ExpectedEpoch=Guid.NewGuid().ToString("N")}))Assert.Equal(HttpStatusCode.Conflict,wrongEpoch.StatusCode);
            if(concurrent)scenes.UpdateAcceptedText(plan.Fingerprint,"Offline edit","Offline summary");
            await publisher.PublishAsync(scope:scope);
            Assert.Equal("远程场景",Assert.Single(remote.GetAllMirroredSceneGroupDescriptions()).SceneName);
            await publisher.PullMetadataChangesAsync(scope:scope);
            var downloaded=Assert.Single(local.GetAllMirroredSceneGroupDescriptions());
            Assert.Equal("远程场景",downloaded.SceneName);
            if(concurrent)
            {
                var conflict=Assert.Single(remote.GetMetadataEditConflicts(),item=>item.Proposed.Kind=="scene-description");
                Assert.Contains("Offline edit",conflict.Proposed.Value);
                Assert.Contains(local.GetMetadataEditConflicts(),item=>item.Proposed.Value.Contains("Offline edit",StringComparison.Ordinal));
                var snapshot=remote.GetMetadataConflictSnapshot();
                remote.ResolveMetadataEditConflict(conflict.OperationId,true,conflict.Current.ServerVersion,conflict.Current.OperationId,true,snapshot.Epoch);
                await publisher.PullMetadataChangesAsync(scope:scope);
                Assert.Equal("Offline edit",Assert.Single(local.GetAllMirroredSceneGroupDescriptions()).SceneName);
            }
            else
            {
                scenes.UpdateAcceptedText(downloaded.Fingerprint,"本地新标题","本地新摘要");
                await publisher.PublishAsync(scope:scope);await publisher.PullMetadataChangesAsync(scope:scope);
                Assert.Equal("本地新标题",Assert.Single(remote.GetAllMirroredSceneGroupDescriptions()).SceneName);
                Assert.Equal(0,await publisher.PublishAsync(scope:scope));
                if(!remoteCreation)
                {
                    using(var backlog=local.Command("""
                        WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM rows WHERE n<6001)
                        INSERT INTO sync_metadata_row_journal(operation_id,table_name,row_key,operation,after_payload)
                        SELECT lower(hex(randomblob(16))),'cloud_vision_analyses','[7000,"local"]','upsert','{}' FROM rows;
                        """))backlog.ExecuteNonQuery();
                    Assert.Equal(0,await publisher.PublishAsync(scope:scope));
                    using var count=local.Command("SELECT COUNT(*) FROM sync_metadata_row_journal WHERE operation='upsert'");
                    Assert.Equal(1001L,count.ExecuteScalar());
                    Assert.Equal(0,await publisher.PublishAsync(scope:scope));Assert.Equal(0L,count.ExecuteScalar());
                }
            }
        }
        finally
        {
            await instance.App.StopAsync();await instance.App.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }
}
