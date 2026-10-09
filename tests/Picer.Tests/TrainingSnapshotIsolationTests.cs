using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class TrainingSnapshotIsolationTests
{
    [Fact]
    public void SnapshotExcludesUnconfirmedSuggestions()
    {
        using var db = new PicerDatabase(":memory:");
        var source = db.AddSource(Path.GetTempPath());
        var people = new PeopleService(db);
        var person = people.Create("fixture");
        for (var i = 1; i <= 2; i++)
        {
            var image = db.UpsertImage(new ImageRecord { SourceId = source.Id,
                Path = Path.Combine(source.Path, $"snapshot-{i}.jpg"), FileSize = i,
                ModifiedUtc = DateTime.UtcNow, Sha256 = new string((char)('a'+i), 64), Width = 100, Height = 100 });
            using var insert = db.Command("""
                INSERT INTO detected_faces(id,image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,embedding,created_utc)
                VALUES($id,$image,'model-a',0,0,0,1,1,1,$vector,$now)
                """);
            insert.Parameters.AddWithValue("$id", i); insert.Parameters.AddWithValue("$image", image.Id);
            insert.Parameters.AddWithValue("$vector", BitConverter.GetBytes((float)i).Concat(BitConverter.GetBytes(1f)).ToArray());
            insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O")); insert.ExecuteNonQuery();
            if (i == 1) people.MoveFace(i, person.Id); else Assert.True(people.SuggestFace(i, person.Id, .8));
        }
        var snapshots = new FaceEvaluationSnapshotService(new VisionAnalysisRepository(db));
        var snapshot = snapshots.CreateSnapshot();
        Assert.Equal(1, Assert.Single(snapshots.GetMembers(snapshot.SnapshotId)).FaceId);
    }

    [Fact]
    public void VideoTrainingQueryRequiresMatchingModelSpace()
    {
        using var db = new PicerDatabase(":memory:");
        var source = db.AddSource(Path.GetTempPath());
        var person = new PeopleService(db).Create("fixture");
        var video = new VideoRecord(10, source.Id, Path.Combine(source.Path, "fixture.mp4"), 1,
            DateTime.UtcNow, null, 0, 0, null, null, "active");
        new VideoRepository(db).UpsertMirrored(video, source.Id);
        var now = DateTime.UtcNow.ToString("O");
        var face = new GatewayVideoFace(10, video.Id, 20, 1, 0, .1, .1, .5, .5, .99,
            BitConverter.GetBytes(1f), null, null, person.Id, "confirmed", now, now) { ModelVersion = "model-a" };
        db.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], [], [], [],
            [new GatewayVideoKeyframe(20, video.Id, 1, "fixture.jpg", 0)], [], [], [face]));
        var repository = new VisionAnalysisRepository(db);
        Assert.Single(repository.GetVideoTrainingFaces("model-a"));
        Assert.Empty(repository.GetVideoTrainingFaces("model-b"));
    }
}
