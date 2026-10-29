using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.Tests;

public sealed class MirrorDifferentialWriteTests
{
    [Fact]
    public void CatalogAndVideoMetadataRepeatWithoutPersistentUpdates()
    {
        using var db = new PicerDatabase(":memory:");
        var source = db.AddSource(Path.GetTempPath());
        var tag = new Tag { Id=1, Name="tag", CreatedUtc=DateTime.UnixEpoch };
        var album = new Album { Id=1, Name="album", CreatedUtc=DateTime.UnixEpoch };
        db.UpsertMirroredTag(tag); db.UpsertMirroredAlbum(album); db.UpsertMirroredPerson(new(1,"person",0,0));
        var videos = new Picer.Core.Video.VideoRepository(db);
        videos.UpsertMirrored(new(1,source.Id,"video.mp4",1,DateTime.UnixEpoch,1,1,1,null,null,"active"),source.Id);
        var metadata = new GatewayMetadata([],[],[]) with
        {
            VideoKeyframes=[new(1,1,0,"frame.jpg",0)],
            VideoTranscripts=[new(1,"model","{}") { UpdatedUtc=DateTime.UnixEpoch.ToString("O") }]
        };
        db.UpsertMirroredVideoMetadata(metadata);
        using (var probe = db.Command("""
            CREATE TABLE catalog_probe(n INTEGER); INSERT INTO catalog_probe VALUES(0);
            CREATE TRIGGER tag_update AFTER UPDATE ON tags BEGIN UPDATE catalog_probe SET n=n+1; END;
            CREATE TRIGGER album_update AFTER UPDATE ON albums BEGIN UPDATE catalog_probe SET n=n+1; END;
            CREATE TRIGGER person_update AFTER UPDATE ON people BEGIN UPDATE catalog_probe SET n=n+1; END;
            CREATE TRIGGER frame_update AFTER UPDATE ON video_keyframes BEGIN UPDATE catalog_probe SET n=n+1; END;
            CREATE TRIGGER transcript_update AFTER UPDATE ON video_transcripts BEGIN UPDATE catalog_probe SET n=n+1; END;
            """)) probe.ExecuteNonQuery();
        db.UpsertMirroredTag(tag); db.UpsertMirroredAlbum(album); db.UpsertMirroredPerson(new(1,"person",0,0));
        db.UpsertMirroredVideoMetadata(metadata);
        using var count = db.Command("SELECT n FROM catalog_probe");
        Assert.Equal(0L, count.ExecuteScalar());
        db.UpsertMirroredPerson(new(1,"changed",0,0));
        Assert.Equal(1L, count.ExecuteScalar());
    }

    [Fact]
    public void TrainingSnapshotSkipsIdenticalBlobsAndRollsBackInvalidReplacement()
    {
        using var db = new PicerDatabase(":memory:");
        db.UpsertMirroredPerson(new(1,"fixture",0,0));
        var metadata = new GatewayMetadata([],[],[]) with
        {
            FaceTrainingArtifacts=[new("artifact","dataset","local","model","{}",.1,.1,"active","time","time")],
            FacePersonPrototypes=[new(1,[1,2,3,4],.7,3,"model","time")],
            FacePersonPolicies=[new(1,"artifact","model",[1,2,3,4],.7,"time")],
            FaceModelSettings=[new("model",.7,3,4,"time")]
        };
        db.UpsertMirroredFaceTrainingMetadata(metadata);
        using (var probe = db.Command("""
            CREATE TABLE training_probe(n INTEGER); INSERT INTO training_probe VALUES(0);
            CREATE TRIGGER training_update AFTER UPDATE ON face_person_prototypes BEGIN UPDATE training_probe SET n=n+1; END;
            CREATE TRIGGER training_delete AFTER DELETE ON face_person_prototypes BEGIN UPDATE training_probe SET n=n+1; END;
            """)) probe.ExecuteNonQuery();
        db.UpsertMirroredFaceTrainingMetadata(metadata with { FacePersonPrototypes=[new(1,[1,2,3,4],.7,3,"model","time")] });
        using var count = db.Command("SELECT n FROM training_probe");
        Assert.Equal(0L, count.ExecuteScalar());
        Assert.Throws<InvalidOperationException>(() => db.UpsertMirroredFaceTrainingMetadata(metadata with
        { FacePersonPrototypes=[], FaceModelSettings=[new("model",.7,3,4,"time"),new("model",.8,3,4,"time")] }));
        Assert.Equal(0L, count.ExecuteScalar());
        db.UpsertMirroredFaceTrainingMetadata(metadata with { FacePersonPrototypes=[new(1,[4,3,2,1],.7,3,"model","later")] });
        Assert.Equal(1L, count.ExecuteScalar());
        db.UpsertMirroredFaceTrainingMetadata(new GatewayMetadata([],[],[]));
        Assert.Equal(2L, count.ExecuteScalar());
    }

    [Fact]
    public void FaceAndWorkflowShareAnAtomicRollbackAndChangedModelsDropOldVectors()
    {
        using var db = new PicerDatabase(":memory:");
        var source = db.AddSource(Path.GetTempPath());
        var image = db.UpsertImage(new ImageRecord { SourceId=source.Id, Path=Path.Combine(Path.GetTempPath(),"atomic-face.jpg"), ModifiedUtc=DateTime.UnixEpoch, IndexedUtc=DateTime.UnixEpoch });
        var face = new GatewayDetectedFace(1,image.Id,"old",0,0,0,1,1,.9,null,[1,2],"time",false);
        db.UpsertMirroredFaces([face],[]);
        using (var transaction = db.BeginTransaction())
        {
            db.UpsertMirroredFaces([face with { ModelVersion="new", Embedding=null }],[],transaction);
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => db.ReplaceMirroredFaceWorkflow(new GatewayMetadata([],[],[])
                { FaceCandidateMembers=[new(999,1,.8)] },transaction));
            transaction.Rollback();
        }
        Assert.Equal("old", Assert.Single(db.GetAllMirroredFaces()).ModelVersion);
        db.UpsertMirroredFaces([face with { ModelVersion="new", Embedding=null }],[]);
        Assert.Null(Assert.Single(db.GetAllMirroredFaces()).Embedding);
    }

    [Fact]
    public void UnchangedFacesAndWorkflowDoNotFireUpdateOrDeleteTriggers()
    {
        using var db = new PicerDatabase(":memory:");
        var source = db.AddSource(Path.GetTempPath());
        var image = db.UpsertImage(new ImageRecord { SourceId=source.Id, Path=Path.Combine(Path.GetTempPath(), "mirror-probe.jpg"), ModifiedUtc=DateTime.UnixEpoch, IndexedUtc=DateTime.UnixEpoch });
        var face = new GatewayDetectedFace(1,image.Id,"model",0,0,0,1,1,.9,null,[1,2,3,4],DateTime.UnixEpoch.ToString("O"),false);
        var metadata = new GatewayMetadata([],[],[]) with
        {
            FaceCandidateClusters=[new(1,"model","time")],
            FaceCandidateMembers=[new(1,1,.8)],
            FaceReviewStates=[new(1,"ignored","time")]
        };
        db.UpsertMirroredFaces([face], []); db.ReplaceMirroredFaceWorkflow(metadata);
        using (var command = db.Command("""
            CREATE TABLE write_probe(n INTEGER); INSERT INTO write_probe VALUES(0);
            CREATE TRIGGER probe_face AFTER UPDATE ON detected_faces BEGIN UPDATE write_probe SET n=n+1; END;
            CREATE TRIGGER probe_review AFTER DELETE ON face_review_states BEGIN UPDATE write_probe SET n=n+1; END;
            CREATE TRIGGER probe_member AFTER DELETE ON face_candidate_members BEGIN UPDATE write_probe SET n=n+1; END;
            CREATE TRIGGER probe_cluster AFTER DELETE ON face_candidate_clusters BEGIN UPDATE write_probe SET n=n+1; END;
            """)) command.ExecuteNonQuery();
        db.UpsertMirroredFaces([face with { Embedding=null }], []);
        db.ReplaceMirroredFaceWorkflow(metadata);
        using var count = db.Command("SELECT n FROM write_probe");
        Assert.Equal(0L, count.ExecuteScalar());
        Assert.Equal(face.Embedding, Assert.Single(db.GetAllMirroredFaces()).Embedding);
        db.ReplaceMirroredFaceWorkflow(metadata with { FaceCandidateMembers=[], FaceReviewStates=[] });
        Assert.Empty(db.GetAllMirroredFaceCandidateMembers());
        Assert.Empty(db.GetAllMirroredFaceReviewStates());
        Assert.Single(db.GetAllMirroredFaceCandidateClusters());
    }
}
