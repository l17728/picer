using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class VideoFaceCausalTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"picer-video-causal-"+Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    private readonly GatewayVideoFace _face;
    private readonly VideoFaceTimelineService _timeline;
    public VideoFaceCausalTests()
    {
        _database=new(Path.Combine(_root,"picer.db"));
        var source=_database.AddSource(_root);
        new VideoRepository(_database).UpsertMirrored(new(1,source.Id,Path.Combine(_root,"video.mp4"),10,DateTime.UnixEpoch,10,100,100,null,null,"active"),source.Id);
        _database.UpsertMirroredPerson(new(1,"测试人物甲",0,0));_database.UpsertMirroredPerson(new(2,"测试人物乙",0,0));
        _face=new(11,1,10,0,0,0,0,10,10,1,null,1,1,null,null,null,DateTime.UtcNow.ToString("O")) { ModelVersion="test-model" };
        _database.UpsertMirroredVideoMetadata(new GatewayMetadata([],[],[]) with { VideoKeyframes=[new(10,1,0,"",0)],VideoFaces=[_face] });
        _timeline=new(_database,new UnusedAnalyzer(),new VisionAnalysisRepository(_database));
    }

    private static MetadataEdit State(long? person,string state,long baseline=0)=>
        new("video-face-state",11,"state",JsonSerializer.Serialize(new FaceManualState(person,state,"test-model")),DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N"))
        { BaseVersion=baseline };

    [Fact]
    public void ConcurrentManualStatesKeepAConflictAndLegacySnapshotsCannotOverwriteTheWinner()
    {
        var initial=Assert.Single(_database.AcceptMetadataEdits([State(1,"confirmed")]));
        var winner=Assert.Single(_database.AcceptMetadataEdits([State(null,"ignored",initial.ServerVersion)]));
        var stale=State(2,"confirmed",initial.ServerVersion);
        Assert.Equal(winner,Assert.Single(_database.AcceptMetadataEdits([stale])));
        Assert.Single(_database.GetMetadataEditConflicts());
        _database.UpsertMirroredVideoMetadata(new GatewayMetadata([],[],[]) with
            { VideoFaces=[_face with { AssignedPersonId=2,AssignmentState="confirmed",UpdatedUtc="2099-01-01T00:00:00.0000000Z" }] });
        var current=Assert.Single(_database.GetAllMirroredVideoFaces());
        Assert.Equal("ignored",current.AssignmentState);Assert.Null(current.AssignedPersonId);
        var snapshot=_database.GetMetadataConflictSnapshot();
        Assert.Equal("忽略此人脸",Assert.Single(snapshot.Reviews).CurrentDisplay);
        _database.ResolveMetadataEditConflict(stale.OperationId,true,winner.ServerVersion,winner.OperationId,true,snapshot.Epoch);
        Assert.Equal(2L,Assert.Single(_database.GetAllMirroredVideoFaces()).AssignedPersonId);
    }

    [Fact]
    public void ConfirmIgnoreWithdrawAndRejectionWithdrawalProduceExplicitModelBoundFields()
    {
        _timeline.RejectSuggestion(11,1);
        Assert.True(PicerDatabase.ParseFacePersonRejection(Assert.Single(_database.GetMetadataEdits()).Value).Rejected);
        _timeline.Confirm(11,1);
        Assert.False(PicerDatabase.ParseFacePersonRejection(_database.GetMetadataEdits().Single(edit=>edit.Kind=="video-face-rejection").Value).Rejected);
        _timeline.MarkIgnored(11);_timeline.ClearManualAssignment(11);
        var state=PicerDatabase.ParseFaceManualState(_database.GetMetadataEdits().Single(edit=>edit.Kind=="video-face-state").Value);
        Assert.Equal("unassigned",state.State);Assert.Equal("test-model",state.ModelVersion);Assert.Null(state.PersonId);
        Assert.Null(Assert.Single(_database.GetAllMirroredVideoFaces()).AssignmentState);
        using var rejected=_database.Command("SELECT COUNT(*) FROM video_face_person_rejections");Assert.Equal(0L,rejected.ExecuteScalar());
    }

    [Fact]
    public void RejectionReplaysWithoutResurrectingSuggestionsAndExplicitPurgeErasesItsReceipts()
    {
        var edit=new MetadataEdit("video-face-rejection",11,"1",JsonSerializer.Serialize(new FacePersonRejection(true,"test-model")),DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N"));
        var accepted=Assert.Single(_database.AcceptMetadataEdits([edit]));
        Assert.Equal(accepted,Assert.Single(_database.AcceptMetadataEdits([edit])));
        _database.UpsertMirroredVideoMetadata(new GatewayMetadata([],[],[]) with { VideoFaces=[_face] });
        Assert.Null(Assert.Single(_database.GetAllMirroredVideoFaces()).SuggestedPersonId);
        Assert.True(new VideoRecordPurger(_database,allowSourceCacheCleanup:false).Purge(1));
        Assert.Empty(_database.GetMetadataEdits());
        using var receipts=_database.Command("SELECT COUNT(*) FROM metadata_causal_receipts WHERE kind='video-face-rejection'");Assert.Equal(0L,receipts.ExecuteScalar());
    }

    [Fact]
    public void WrongModelDoesNotApplyOrCreateAReceipt()
    {
        var edit=State(1,"confirmed") with { Value=JsonSerializer.Serialize(new FaceManualState(1,"confirmed","other-model")) };
        Assert.Empty(_database.AcceptMetadataEdits([edit]));Assert.Empty(_database.GetMetadataEdits());
        Assert.Null(Assert.Single(_database.GetAllMirroredVideoFaces()).AssignedPersonId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersonMergeTransfersRejectionsAndPreservesConfirmedAssignments(bool confirmed)
    {
        _timeline.RejectSuggestion(11,1);
        if(confirmed) _timeline.Confirm(11,2);
        new Picer.Core.People.PeopleService(_database).Merge(1,2);
        var rejection=Assert.Single(_database.GetMetadataEdits().Where(edit=>edit.Kind=="video-face-rejection"));
        Assert.Equal("2",rejection.Field);
        Assert.Equal(!confirmed,PicerDatabase.ParseFacePersonRejection(rejection.Value).Rejected);
        Assert.DoesNotContain(new Picer.Core.People.PeopleService(_database).GetAll(),person=>person.Id==1);
        _database.ReplayMetadataEdits();
        if(confirmed) Assert.Equal(2L,Assert.Single(_database.GetAllMirroredVideoFaces()).AssignedPersonId);
        using var count=_database.Command("SELECT COUNT(*) FROM video_face_person_rejections WHERE person_id=2");
        Assert.Equal(confirmed?0L:1L,count.ExecuteScalar());
    }

    [Fact]
    public void PersonMergeRecordsMovedVideoAssignmentInsteadOfLeavingTheDeletedPersonInItsField()
    {
        _timeline.Confirm(11,1);
        new Picer.Core.People.PeopleService(_database).Merge(1,2);
        var state=PicerDatabase.ParseFaceManualState(Assert.Single(_database.GetMetadataEdits().Where(edit=>edit.Kind=="video-face-state")).Value);
        Assert.Equal(2L,state.PersonId);
        _database.ReplayMetadataEdits();
        Assert.Equal(2L,Assert.Single(_database.GetAllMirroredVideoFaces()).AssignedPersonId);
    }

    [Fact]
    public void DeletingPersonRemovesVideoRejectionConflictAndRetryReceipts()
    {
        var request=new MetadataEdit("video-face-rejection",11,"1",JsonSerializer.Serialize(new FacePersonRejection(true,"test-model")),DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N"));
        _database.AcceptMetadataEdits([request]);
        _database.AcceptMetadataEdits([request with { OperationId=Guid.NewGuid().ToString("N"),Value=JsonSerializer.Serialize(new FacePersonRejection(false,"test-model")) }]);
        Assert.Single(_database.GetMetadataEditConflicts());
        using(var delete=_database.Command("DELETE FROM people WHERE id=1")) delete.ExecuteNonQuery();
        Assert.Empty(_database.GetMetadataEditConflicts());Assert.Empty(_database.GetMetadataEdits());
        using var receipts=_database.Command("SELECT COUNT(*) FROM metadata_causal_receipts WHERE kind='video-face-rejection'");
        Assert.Equal(0L,receipts.ExecuteScalar());
        Assert.Empty(_database.AcceptMetadataEdits([request]));
    }

    [Fact]
    public async Task ReanalysisWithNoDetectionsRetainsManualHistoryInsteadOfDeletingIt()
    {
        var frame=Path.Combine(_root,"frame.jpg");await File.WriteAllBytesAsync(frame,[1,2,3]);
        using(var path=_database.Command("UPDATE video_keyframes SET path=$path WHERE id=10"))
        { path.Parameters.AddWithValue("$path",frame);path.ExecuteNonQuery(); }
        _timeline.MarkIgnored(11);
        var analysis=new VideoFaceTimelineService(_database,new EmptyAnalyzer(),new VisionAnalysisRepository(_database));
        await analysis.AnalyzeAsync(new VideoRepository(_database).Get(1)!);
        Assert.Equal("ignored",Assert.Single(_database.GetAllMirroredVideoFaces()).AssignmentState);
        _timeline.ClearManualAssignment(11);
        await analysis.AnalyzeAsync(new VideoRepository(_database).Get(1)!);
        Assert.Single(_database.GetAllMirroredVideoFaces());
        Assert.Equal("unassigned",PicerDatabase.ParseFaceManualState(Assert.Single(_database.GetMetadataEdits()).Value).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpgradeSeedsOnlyOwnedHistoricalManualStatesAndRejections(bool ambiguous)
    {
        using(var prepare=_database.Command("""
            UPDATE video_detected_faces SET assignment_state='ignored' WHERE id=11;
            INSERT INTO video_face_person_rejections VALUES(11,1,'2026-01-01');
            DROP TRIGGER space_sync_video_face_field_purge;
            DROP TRIGGER space_sync_video_person_rejection_purge;
            DROP TRIGGER space_sync_video_face_field_notify_INSERT;
            DROP TRIGGER space_sync_video_face_field_notify_UPDATE;
            PRAGMA user_version=71;
            """)) prepare.ExecuteNonQuery();
        if(ambiguous)
        {
            using var quarantine=_database.Command("UPDATE video_detected_faces SET owner_key='ambiguous-content:test' WHERE id=11");quarantine.ExecuteNonQuery();
        }
        var path=_database.DatabasePath;_database.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var migrated=new PicerDatabase(path);
        var fields=migrated.GetMetadataEdits();
        if(ambiguous) Assert.Empty(fields);
        else
        {
            Assert.Equal(2,fields.Count);
            Assert.Equal("ignored",PicerDatabase.ParseFaceManualState(fields.Single(edit=>edit.Kind=="video-face-state").Value).State);
            Assert.True(PicerDatabase.ParseFacePersonRejection(fields.Single(edit=>edit.Kind=="video-face-rejection").Value).Rejected);
            var accepted=migrated.AcceptMetadataEdits(fields);Assert.Equal(2,accepted.Count);
            Assert.Equal(accepted,migrated.AcceptMetadataEdits(fields));
        }
    }

    private sealed class EmptyAnalyzer : IImageFaceAnalyzer
    {
        public string AnalyzerName=>"test";
        public string ModelVersion=>"test-model";
        public Task<FaceAnalysisResult> AnalyzeAsync(Picer.Core.Models.ImageRecord image,CancellationToken cancellationToken)
            =>Task.FromResult(new FaceAnalysisResult([]) { ModelVersion="test-model" });
    }

    private sealed class UnusedAnalyzer : IImageFaceAnalyzer
    {
        public string AnalyzerName=>"test";
        public string ModelVersion=>"test-model";
        public Task<FaceAnalysisResult> AnalyzeAsync(Picer.Core.Models.ImageRecord image,CancellationToken cancellationToken)
            =>throw new InvalidOperationException("Existing detections only.");
    }

    public void Dispose()
    {
        _database.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(_root,true);
    }
}
