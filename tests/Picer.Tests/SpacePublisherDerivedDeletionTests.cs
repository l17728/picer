using System.Security.Cryptography;
using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.IndexService;
using Picer.IndexService.Identity;

namespace Picer.Tests;

public sealed partial class SpacePublisherIntegrationTests
{
    [Theory]
    [InlineData("image_classification_assertions",false)]
    [InlineData("image_classification_assertions",true)]
    [InlineData("semantic_content_cluster_members",false)]
    [InlineData("semantic_content_cluster_members",true)]
    [InlineData("cloud_vision_analyses",false)]
    [InlineData("cloud_vision_analyses",true)]
    [InlineData("image_locations",false)]
    [InlineData("image_locations",true)]
    [InlineData("detected_faces",false)]
    [InlineData("detected_faces",true)]
    [InlineData("video_summaries",false)]
    [InlineData("video_summaries",true)]
    [InlineData("video_transcripts",false)]
    [InlineData("video_transcripts",true)]
    [InlineData("video_keyframes",false)]
    [InlineData("video_keyframes",true)]
    [InlineData("video_detected_faces",false)]
    [InlineData("video_detected_faces",true)]
    [InlineData("detected_faces",false,true)]
    [InlineData("video_detected_faces",false,true)]
    [InlineData("image_classification_assertions",false,false,true)]
    [InlineData("video_transcripts",false,false,true)]
    [InlineData("image_classification_assertions",false,false,false,true)]
    [InlineData("image_classification_assertions",false,false,false,false,true)]
    [InlineData("face_candidate_members",false)]
    [InlineData("face_candidate_members",true)]
    public async Task ModelRecomputationDeletesOnlyUnchangedPublishedSuggestions(string table,bool remoteReview,bool manual=false,bool disconnect=false,bool replacement=false,bool otherPublisher=false)
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-derived-api-"+Guid.NewGuid().ToString("N"));
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
            var video=table.StartsWith("video_",StringComparison.Ordinal);
            if(video) new Picer.Core.Video.VideoRepository(local).UpsertMirrored(new(7000,source.Id,@"C:\Photos\one.mp4",100,DateTime.UnixEpoch,1,10,10,null,null,"active"),source.Id);
            local.UpsertMirroredImage(new ImageRecord { Id=7000,SourceId=source.Id,Path=@"C:\Photos\one.jpg",FileSize=100,
                ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
            var seedSql=table switch
            {
                "image_classification_assertions"=>"""
                INSERT INTO image_classification_assertions(image_id,concept_id,source_kind,source_version,assertion_state,review_state,confidence,evidence_fingerprint,created_utc,updated_utc)
                VALUES(7000,'scene.mountain','vision_model','model-1','suggested','pending',0.9,'evidence','2026-10-25','2026-10-25');
                """,
                "semantic_content_cluster_members"=>"""
                INSERT INTO semantic_content_clusters(cluster_key,model_version,display_name,confidence,member_count,created_utc,updated_utc)
                VALUES('test-cluster','model-1','山景',0.9,1,'2026-10-25','2026-10-25');
                INSERT INTO semantic_content_cluster_members(cluster_key,image_id,similarity) VALUES('test-cluster',7000,0.9);
                """,
                "cloud_vision_analyses"=>"""
                INSERT INTO cloud_vision_analyses(image_id,provider,model_version,prompt_version,source_file_size,source_modified_utc,location_fingerprint,status,summary,result_json,updated_utc)
                VALUES(7000,'local','model-1','prompt-1',100,'1970-01-01T00:00:00.0000000Z','','completed','山景','{}','2026-10-25');
                """,
                "image_locations"=>"""
                INSERT INTO image_locations(image_id,latitude,longitude,display_name,search_text,resolved_utc)
                VALUES(7000,20,30,'位置','位置','2026-10-25');
                """,
                "detected_faces"=>"""
                INSERT INTO detected_faces(id,image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc)
                VALUES(71,7000,'face-model-1',0,0,0,10,10,0.9,'2026-10-25');
                """,
                "face_candidate_members"=>"""
                INSERT INTO detected_faces(id,image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc)
                VALUES(71,7000,'face-model-1',0,0,0,10,10,0.9,'2026-10-25');
                INSERT INTO face_candidate_clusters(id,model_version,created_utc) VALUES(72,'face-model-1','2026-10-25');
                INSERT INTO face_candidate_members(cluster_id,face_id,similarity) VALUES(72,71,0.9);
                """,
                "video_summaries"=>"""
                INSERT INTO video_summaries(video_id,provider,model_version,prompt_version,source_file_size,source_modified_utc,summary,result_json,created_utc)
                SELECT content_id,'local','model-1','prompt-1',100,'1970-01-01T00:00:00.0000000Z','山景','{}','2026-10-25' FROM videos WHERE id=7000;
                """,
                "video_transcripts"=>"""
                INSERT INTO video_transcripts(video_id,model_version,transcript_json,created_utc)
                SELECT content_id,'model-1','[]','2026-10-25' FROM videos WHERE id=7000;
                """,
                "video_keyframes" or "video_detected_faces"=>"""
                INSERT INTO video_keyframes(id,video_id,timestamp_seconds,path,dhash,created_utc)
                SELECT 71,content_id,1,'',987654321012345678,'2026-10-25' FROM videos WHERE id=7000;
                """,
                _=>throw new ArgumentException(nameof(table))
            };
            using(var seed=local.Command(seedSql)) seed.ExecuteNonQuery();
            if(table=="video_detected_faces")
            {
                using var face=local.Command("""
                    INSERT INTO video_detected_faces(id,video_id,keyframe_id,timestamp_seconds,ordinal,model_version,owner_key,box_x,box_y,box_width,box_height,confidence,created_utc)
                    SELECT 72,content_id,71,1,0,'face-model-1',owner_key,0,0,10,10,0.9,'2026-10-25' FROM sync_video_owners WHERE video_id=7000;
                    """);face.ExecuteNonQuery();
            }
            var publisher=new SpaceSyncPublisher(client,local,Path.Combine(root,"snapshots"));var scope=new SpaceUploadScope([source.Id],People:true,PreciseLocation:true);
            await publisher.PublishAsync(scope:scope);
            await publisher.PullMetadataChangesAsync(scope:scope);
            using var remote=new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id),"picer.db"));
            using(var count=remote.Command($"SELECT COUNT(*) FROM {table}")) Assert.Equal(1L,count.ExecuteScalar());
            if(remoteReview)
            {
                var update=table switch
                {
                    "image_classification_assertions"=>"review_state='accepted',updated_utc='2026-10-26'",
                    "semantic_content_cluster_members"=>"similarity=0.8",
                    "cloud_vision_analyses"=>"summary='新描述',updated_utc='2026-10-26'",
                    "detected_faces"=>"box_width=20",
                    "video_summaries"=>"summary='新描述',created_utc='2026-10-26'",
                    "video_transcripts"=>"transcript_json='[1]',created_utc='2026-10-26'",
                    "video_keyframes"=>"dhash=2",
                    "video_detected_faces"=>"box_width=20",
                    "face_candidate_members"=>"similarity=0.8",
                    _=>"country='新位置',resolved_utc='2026-10-26'"
                };
                using var review=remote.Command($"UPDATE {table} SET {update}");review.ExecuteNonQuery();
            }
            if(manual)
            {
                using var review=remote.Command(video
                    ?"UPDATE video_detected_faces SET assignment_state='ignored',updated_utc='2026-10-26'"
                    :"INSERT INTO face_review_states SELECT id,'ignored','2026-10-26' FROM detected_faces");review.ExecuteNonQuery();
            }
            if(otherPublisher)
            {
                using var another=local.Command("""
                    INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
                    SELECT $other,library_id,$other,$other,space_id,'rebaselining',epoch FROM sync_publishers LIMIT 1;
                    INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id)
                    VALUES($other,'image','7000',$global);
                    """);another.Parameters.AddWithValue("$other",Guid.NewGuid().ToString("N"));another.Parameters.AddWithValue("$global",Guid.NewGuid().ToString("N"));another.ExecuteNonQuery();
            }
            using(var delete=local.Command($"DELETE FROM {table}")) delete.ExecuteNonQuery();
            if(replacement) {using var replace=local.Command(seedSql);replace.ExecuteNonQuery();}
            await publisher.PublishAsync(scope:video?scope with {Videos=false}:scope with {Photos=false});
            using(var count=remote.Command($"SELECT COUNT(*) FROM {table}")) Assert.Equal(1L,count.ExecuteScalar());
            if(disconnect)
            {
                bridge.AfterCommittedBatch=batch=>
                {
                    if(batch.Kind!="derived-deletions")return;
                    bridge.AfterCommittedBatch=null;bridge.FailNextCommittedBatch=true;
                };
                await Assert.ThrowsAsync<HttpRequestException>(()=>publisher.PublishAsync(scope:scope));
                publisher=new SpaceSyncPublisher(client,local,Path.Combine(root,"snapshots"));
            }
            await publisher.PublishAsync(scope:scope);
            using(var count=remote.Command($"SELECT COUNT(*) FROM {table}")) Assert.Equal(remoteReview||manual||replacement?1L:0L,count.ExecuteScalar());
            await publisher.PullMetadataChangesAsync(scope:scope);
            using(var count=local.Command($"SELECT COUNT(*) FROM {table}")) Assert.Equal(remoteReview||manual||replacement?1L:0L,count.ExecuteScalar());
            Assert.Equal(0,await publisher.PublishAsync(scope:scope));
            using(var journal=local.Command("SELECT COUNT(*) FROM sync_metadata_row_journal WHERE table_name=$table AND operation='delete'"))
            {journal.Parameters.AddWithValue("$table",table);Assert.Equal(otherPublisher?1L:0L,journal.ExecuteScalar());}
        }
        finally
        {
            await instance.App.StopAsync();await instance.App.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }
}
