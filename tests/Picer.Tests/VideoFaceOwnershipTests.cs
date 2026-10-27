using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Identity;
using Picer.Core.People;
using Picer.Core.Video;
using Picer.Core.Vision;
using Picer.IndexService.Identity;
using Microsoft.Data.Sqlite;

namespace Picer.Tests;

public sealed class VideoFaceOwnershipTests : IDisposable
{
    [Fact]
    public void OwnershipReadsParticipateInTheExistingTransaction()
    {
        var owner=_database.GetVideoOwnerKey(1);
        using var transaction=_database.BeginTransaction();
        Assert.Equal(owner,_database.GetVideoOwnerKey(1));
        _database.RequireOwnedVideoFace(101);
        transaction.Rollback();
        Assert.Equal(owner,_database.GetVideoOwnerKey(1));
    }

    private readonly string _root=Path.Combine(Path.GetTempPath(),"picer-video-face-owners-"+Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    public VideoFaceOwnershipTests()
    {
        _database=new(Path.Combine(_root,"picer.db"));
        var owners=new[]{Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N")};
        for(var owner=0;owner<2;owner++)
        {
            var source=_database.AddSource(PublishedMediaPath.Qualify(owners[owner],@"C:\Photos"));
            foreach(var id in owner==0?new long[]{1,2}:new long[]{3})
                new VideoRepository(_database).UpsertMirrored(new(id,source.Id,PublishedMediaPath.Qualify(owners[owner],$@"C:\Photos\{id}.mp4"),
                    10,DateTime.UnixEpoch,30,100,100,null,null,"active") { Sha256=new string('a',64) },source.Id);
        }
        _database.UpsertMirroredPerson(new(1,"来源一测试人物",0,0));_database.UpsertMirroredPerson(new(2,"来源二测试人物",0,0));
        var now=DateTime.UtcNow.ToString("O");
        _database.UpsertMirroredVideoMetadata(new GatewayMetadata([],[],[]) with
        {
            VideoKeyframes=[new(100,1,0,"",123)],
            VideoFaces=[new(101,1,100,0,0,0,0,10,10,0.9,null,null,null,1,"confirmed",now,now) { ModelVersion="test-model" },
                new(102,3,100,0,0,0,0,10,10,0.9,null,null,null,2,"confirmed",now,now) { ModelVersion="test-model" }]
        });
    }

    [Fact]
    public void PeopleSearchAndLastOwnerPurgePreserveOtherOwnersAndSharedFrames()
    {
        var search=new VideoSearchMetadata(_database);
        Assert.Equal(new long[]{1,2},search.Match([1],[],[]).Order().ToArray());Assert.Equal(new long[]{3},search.Match([2],[],[]).ToArray());
        Assert.Equal("来源二测试人物",Assert.Single(new PeopleService(_database).GetConfirmedPersonNamesForVideo(3)));
        Assert.Equal(2,_database.GetAllMirroredVideoFaces().Count);
        var purge=new VideoRecordPurger(_database,allowSourceCacheCleanup:false);
        Assert.True(purge.Purge(1));Assert.Equal(2,_database.GetAllMirroredVideoFaces().Count);
        Assert.True(purge.Purge(2));Assert.Equal(102,Assert.Single(_database.GetAllMirroredVideoFaces()).Id);
        Assert.Single(_database.GetAllMirroredVideoKeyframes());Assert.Single(new VideoRepository(_database).GetAll());
        using var journal=_database.Command("SELECT COUNT(*) FROM sync_metadata_row_journal WHERE table_name='video_detected_faces' AND COALESCE(json_extract(after_payload,'$.id'),json_extract(before_payload,'$.id'))=101");
        Assert.Equal(0L,journal.ExecuteScalar());
        Assert.True(purge.Purge(3));Assert.Empty(_database.GetAllMirroredVideoFaces());Assert.Empty(_database.GetAllMirroredVideoKeyframes());
    }

    [Fact]
    public void HistoricalFaceRequiresExplicitOwnerChoiceAndRetriesAreIdempotent()
    {
        AddHistoricalFace(1);
        var review=Assert.Single(_database.GetVideoOwnershipReviews());Assert.Equal("video-face",review.Kind);
        Assert.Equal(2,review.Candidates.Count);
        var selected=review.Candidates.Single(candidate=>candidate.VideoId==3);
        var request=new VideoOwnershipResolution(review.OperationId,[new(selected.VideoId,selected.Version,selected.CurrentOperationId)],Epoch:review.Epoch);
        _database.ResolveVideoOwnershipReview(request,true);_database.ResolveVideoOwnershipReview(request,true);
        Assert.Empty(_database.GetVideoOwnershipReviews());
        using var first=_database.Command("SELECT COUNT(*) FROM video_effective_faces WHERE reference_id=1");Assert.Equal(1L,first.ExecuteScalar());
        using var second=_database.Command("SELECT COUNT(*) FROM video_effective_faces WHERE reference_id=3");Assert.Equal(2L,second.ExecuteScalar());
        Assert.Contains(3L,new VideoSearchMetadata(_database).Match([1],[],[]));
    }

    [Fact]
    public void ConcurrentDecisionChangesFenceHistoricalRestoreAndDiscardKeepsCurrentData()
    {
        AddHistoricalFace(0);
        var review=Assert.Single(_database.GetVideoOwnershipReviews());var selected=review.Candidates.Single(candidate=>candidate.VideoId==1);
        using(var change=_database.Command("UPDATE video_detected_faces SET assignment_state='ignored',assigned_person_id=NULL,updated_utc='2030-01-01' WHERE id=101")) change.ExecuteNonQuery();
        Assert.Throws<InvalidOperationException>(()=>_database.ResolveVideoOwnershipReview(new(review.OperationId,
            [new(selected.VideoId,selected.Version,selected.CurrentOperationId)],Epoch:review.Epoch),true));
        _database.ResolveVideoOwnershipReview(new(review.OperationId,[],Discard:true,Epoch:review.Epoch),true);
        Assert.Empty(_database.GetVideoOwnershipReviews());
        Assert.Equal("ignored",_database.GetAllMirroredVideoFaces().Single(face=>face.Id==101).AssignmentState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LearningHashPreservesFacesAndRejectionsAndQuarantinesSameOwnerCollisions(bool sameOwner)
    {
        var owner=sameOwner?_database.GetVideoOwnerKey(1)["publisher:".Length..]:Guid.NewGuid().ToString("N");
        var source=_database.AddSource(PublishedMediaPath.Qualify(owner,@"C:\Other"));
        new VideoRepository(_database).UpsertMirrored(new(4,source.Id,PublishedMediaPath.Qualify(owner,@"C:\Other\video.mp4"),
            10,DateTime.UnixEpoch,30,100,100,null,null,"active"),source.Id);
        var now=DateTime.UtcNow.ToString("O");
        _database.UpsertMirroredVideoMetadata(new GatewayMetadata([],[],[]) with
        {
            VideoKeyframes=[new(200,4,0,"",123)],
            VideoFaces=[new(201,4,200,0,0,0,0,10,10,0.9,null,null,null,1,"confirmed",now,now) { ModelVersion="test-model" }]
        });
        using(var reject=_database.Command("INSERT INTO video_face_person_rejections(video_face_id,person_id,created_utc) VALUES(201,2,$now)"))
        { reject.Parameters.AddWithValue("$now",now);reject.ExecuteNonQuery(); }
        using(var hash=_database.Command("UPDATE videos SET sha256=$hash WHERE id=4"))
        { hash.Parameters.AddWithValue("$hash",new string('a',64));hash.ExecuteNonQuery(); }
        Assert.Equal(sameOwner?2:3,_database.GetAllMirroredVideoFaces().Count);
        Assert.Single(_database.GetAllMirroredVideoKeyframes());
        using var face=_database.Command("SELECT id FROM video_effective_faces WHERE reference_id=4");
        Assert.Equal(sameOwner?101L:201L,face.ExecuteScalar());
        if(sameOwner) Assert.Equal("video-face",Assert.Single(_database.GetVideoOwnershipReviews()).Kind);
        else Assert.Empty(_database.GetVideoOwnershipReviews());
        using var rejections=_database.Command("SELECT video_face_id FROM video_face_person_rejections WHERE person_id=2");
        Assert.Equal(201L,rejections.ExecuteScalar());
        using var integrity=_database.Command("PRAGMA foreign_key_check");
        using var rows=integrity.ExecuteReader();Assert.False(rows.Read());
    }

    [Fact]
    public void BackfillRejectsAStaleOrDifferentOwnersFaceBeforeWritingAnyVectors()
    {
        var timeline=new VideoFaceTimelineService(_database,new UnusedAnalyzer(),new VisionAnalysisRepository(_database));
        var source=timeline.GetFacesForBackfill("test-model").Single(face=>face.FaceId==101);
        Assert.Throws<InvalidOperationException>(()=>timeline.SaveBackfilledEmbeddings("test-model","new-model",[(source with { VideoId=3 },new float[]{1,0})]));
        Assert.Throws<InvalidOperationException>(()=>timeline.SaveBackfilledEmbeddings("wrong-model","new-model",[(source,new float[]{1,0})]));
        Assert.Equal(2,_database.GetAllMirroredVideoFaces().Count);
        Assert.Equal(1,timeline.SaveBackfilledEmbeddings("test-model","new-model",[(source,new float[]{1,0})]));
        var created=_database.GetAllMirroredVideoFaces().Single(face=>face.ModelVersion=="new-model");
        Assert.Equal(1L,created.AssignedPersonId);Assert.Equal(1L,created.VideoId);
    }

    [Fact]
    public void ServerPurgeForgetsOnlyTheDepartingPublishersSharedKeyframeMapping()
    {
        using(var seed=_database.Command("""
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch) VALUES
                ('a','a','a','a','space','active','epoch'),('b','b','b','b','space','active','epoch');
            INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id) VALUES
                ('a','video','1','a-video-1',1),('a','video','2','a-video-2',2),('b','video','3','b-video',3),
                ('a','keyframe','100','a-frame',100),('b','keyframe','100','b-frame',100);
            """)) seed.ExecuteNonQuery();
        var guard=new RestoreGuard(Path.Combine(_root,"guard"),Guid.NewGuid().ToString("N"),true);
        var space=Guid.NewGuid().ToString("N");
        guard.InitializeNewSpace(space);var purge=new SpacePurgeCoordinator(_database,guard,space);
        purge.Purge("video",1);
        using var mappings=_database.Command("SELECT COUNT(*) FROM sync_entity_identities WHERE kind='keyframe'");
        Assert.Equal(2L,mappings.ExecuteScalar());
        purge.Purge("video",2);Assert.Equal(1L,mappings.ExecuteScalar());
        using var remaining=_database.Command("SELECT publisher_id FROM sync_entity_identities WHERE kind='keyframe'");
        Assert.Equal("b",remaining.ExecuteScalar());
        Assert.Single(_database.GetAllMirroredVideoKeyframes());
        Assert.Equal(102L,Assert.Single(_database.GetAllMirroredVideoFaces()).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MigratingPreOwnerFaceTablePreservesIdsAndQuarantinesAmbiguousDecisions(bool multipleOwners)
    {
        using(var prepare=_database.Command("DELETE FROM video_detected_faces WHERE id=102;"+
            (multipleOwners?"":"DELETE FROM videos WHERE id=3;")+"INSERT INTO video_face_person_rejections VALUES(101,2,'2026-01-01');"))
            prepare.ExecuteNonQuery();
        var path=_database.DatabasePath;_database.Dispose();SqliteConnection.ClearAllPools();
        using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=path,Pooling=false }.ToString()))
        {
            connection.Open();
            void Execute(string sql) { using var command=connection.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery(); }
            Execute("PRAGMA foreign_keys=OFF; PRAGMA legacy_alter_table=ON;");
            string schema;var objects=new List<(string Type,string Name,string Sql)>();var columns=new List<string>();
            using(var query=connection.CreateCommand())
            {
                query.CommandText="SELECT sql FROM sqlite_master WHERE type='table' AND name='video_detected_faces'";
                schema=(string)query.ExecuteScalar()!;
                query.CommandText="SELECT type,name,sql FROM sqlite_master WHERE tbl_name='video_detected_faces' AND type IN ('index','trigger') AND sql IS NOT NULL";
                using(var rows=query.ExecuteReader()) while(rows.Read()) objects.Add((rows.GetString(0),rows.GetString(1),rows.GetString(2)));
                query.CommandText="PRAGMA table_info(video_detected_faces)";
                using(var rows=query.ExecuteReader()) while(rows.Read()) if(rows.GetString(1)!="owner_key") columns.Add(rows.GetString(1));
            }
            Execute("DROP VIEW video_effective_faces; DROP TABLE video_face_ownership_reviews;");
            foreach(var item in objects) Execute($"DROP {item.Type} \"{item.Name}\"");
            Execute("ALTER TABLE video_detected_faces RENAME TO temporary_owner_faces");
            schema=schema.Replace("owner_key TEXT NOT NULL DEFAULT '', ","",StringComparison.Ordinal)
                .Replace("UNIQUE(keyframe_id,model_version,ordinal,owner_key)","UNIQUE(keyframe_id,model_version,ordinal)",StringComparison.Ordinal);
            Assert.DoesNotContain("owner_key",schema);
            Execute(schema);
            Execute($"INSERT INTO video_detected_faces({string.Join(',',columns)}) SELECT {string.Join(',',columns)} FROM temporary_owner_faces; DROP TABLE temporary_owner_faces;");
            foreach(var item in objects.Where(item=>item.Name is not ("ix_video_face_owner" or "video_face_owner_default"))) Execute(item.Sql);
            Execute("PRAGMA user_version=70; PRAGMA legacy_alter_table=OFF;");
        }
        using var migrated=new PicerDatabase(path);
        if(multipleOwners)
        {
            Assert.Empty(migrated.GetAllMirroredVideoFaces());Assert.Single(migrated.GetVideoOwnershipReviews());
        }
        else
        {
            Assert.Equal(101L,Assert.Single(migrated.GetAllMirroredVideoFaces()).Id);Assert.Empty(migrated.GetVideoOwnershipReviews());
        }
        using var rejected=migrated.Command("SELECT person_id FROM video_face_person_rejections WHERE video_face_id=101");Assert.Equal(2L,rejected.ExecuteScalar());
        using var integrity=migrated.Command("PRAGMA foreign_key_check");Assert.Null(integrity.ExecuteScalar());
    }

    private sealed class UnusedAnalyzer : IImageFaceAnalyzer
    {
        public string AnalyzerName=>"test";
        public string ModelVersion=>"test-model";
        public Task<FaceAnalysisResult> AnalyzeAsync(Picer.Core.Models.ImageRecord image,CancellationToken cancellationToken)
            =>throw new InvalidOperationException("This test must use existing detections.");
    }

    private void AddHistoricalFace(int ordinal)
    {
        using var insert=_database.Command("""
            INSERT INTO video_detected_faces(video_id,keyframe_id,timestamp_seconds,ordinal,model_version,box_x,box_y,box_width,box_height,
                confidence,assigned_person_id,assignment_state,updated_utc,created_utc)
            SELECT video_id,id,0,$ordinal,'test-model',0,0,10,10,0.9,1,'confirmed',$now,$now FROM video_keyframes WHERE id=100;
            """);insert.Parameters.AddWithValue("$ordinal",ordinal);insert.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));insert.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _database.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(_root,true);
    }
}
