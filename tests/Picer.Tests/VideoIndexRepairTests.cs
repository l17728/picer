using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class VideoIndexRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-video-repair-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly VideoRepository _videos;
    public VideoIndexRepairTests()
    {
        _db = new(Path.Combine(_root, "picer.db"));
        _videos = new(_db);
    }

    private VideoRecord Seed()
    {
        var old = Path.Combine(_root, "old"); Directory.CreateDirectory(old);
        var path = Path.Combine(old, "clip.mp4"); File.WriteAllBytes(path, [1, 2, 3]);
        var video = _videos.Upsert(_db.AddSource(old).Id, path);
        using var evidence = _db.Command("""
            INSERT INTO video_summaries(video_id,provider,model_version,prompt_version,source_file_size,source_modified_utc,summary,result_json,created_utc)
            VALUES($id,'test','v1','v1',3,$modified,'preserved','{}',$modified);
            INSERT INTO video_transcripts VALUES($id,'v1','preserved transcript',$modified);
            INSERT INTO video_keyframes(video_id,timestamp_seconds,path,created_utc) VALUES($id,0,'cached-keyframe.jpg',$modified);
            INSERT INTO video_detected_faces(video_id,keyframe_id,timestamp_seconds,ordinal,model_version,box_x,box_y,box_width,box_height,confidence,created_utc,assignment_state)
            SELECT $id,id,0,0,'test',0,0,1,1,1,$modified,'ignored' FROM video_keyframes WHERE video_id=$id;
            INSERT INTO people(display_name,created_utc,updated_utc) VALUES('test-person',$modified,$modified);
            INSERT INTO video_detected_faces(video_id,keyframe_id,timestamp_seconds,ordinal,model_version,box_x,box_y,box_width,box_height,confidence,created_utc,assigned_person_id,assignment_state)
            SELECT $id,id,0,1,'test',0,0,1,1,1,$modified,(SELECT MAX(id) FROM people),'confirmed' FROM video_keyframes WHERE video_id=$id;
            INSERT INTO video_face_person_rejections(video_face_id,person_id,created_utc)
            SELECT id,(SELECT MAX(id) FROM people),$modified FROM video_detected_faces WHERE video_id=$id AND ordinal=0;
            INSERT INTO tags(name,created_utc) VALUES('test-tag',$modified);
            INSERT INTO video_tags VALUES($id,(SELECT MAX(id) FROM tags));
            INSERT INTO video_visual_concepts VALUES($id,'test-concept','manual','accepted');
            """);
        evidence.Parameters.AddWithValue("$id", video.Id); evidence.Parameters.AddWithValue("$modified", video.ModifiedUtc.ToString("o")); evidence.ExecuteNonQuery();
        return video;
    }

    [Fact]
    public async Task MovedVideoRetainsIdSummaryTranscriptAndFaceReviewWithNewMtime()
    {
        var video = Seed(); var target = Path.Combine(_root, "new"); Directory.CreateDirectory(target);
        var newPath = Path.Combine(target, "renamed.mp4"); File.Move(video.Path, newPath);
        File.SetLastWriteTimeUtc(newPath, video.ModifiedUtc.AddMinutes(5));
        var operation = Assert.Single((await IndexRepairService.BuildAsync([], [target], videos: _videos.GetAll())).Operations);
        Assert.True(operation.IsVideo);
        var result = IndexRepairService.Apply(_db, operation, await IndexRepairService.VerifyAsync(operation));
        Assert.Equal("restored", result.Status); Assert.Equal(video.Id, result.ImageId);
        var updated = Assert.Single(_videos.GetAll()); Assert.Equal(newPath, updated.Path);
        Assert.True(_videos.HasCurrentSummary(updated)); Assert.Equal("preserved transcript", _videos.GetTranscript(video.Id));
        using var face = _db.Command("SELECT assignment_state FROM video_detected_faces WHERE video_id=$id AND ordinal=0");
        face.Parameters.AddWithValue("$id", video.Id); Assert.Equal("ignored", face.ExecuteScalar());
    }

    [Fact]
    public async Task DuplicateVideoCopiesRetainIndependentPositionsAndDerivedEvidence()
    {
        var video = Seed(); var target = Path.Combine(_root, "new"); Directory.CreateDirectory(target);
        using (var cost = _db.Command("""
            UPDATE video_summaries SET input_tokens=100,output_tokens=20,estimated_cost_yuan=1,
                result_json='{"Summary":"preserved","Scenes":[],"Activities":[],"Objects":[],"SearchTerms":[],"usage":{"inputTokens":100,"outputTokens":20,"estimatedCostYuan":1}}'
            WHERE video_id=$id
            """))
        { cost.Parameters.AddWithValue("$id", video.Id); cost.ExecuteNonQuery(); }
        // This fixture inserts historical evidence directly, so seed its request ledger too.
        using (var billing = _db.Command("INSERT INTO analysis_billing_events VALUES('original-request','test','v1',100,20,1,'2026-10-09T00:00:00Z')"))
            billing.ExecuteNonQuery();
        File.Move(video.Path, Path.Combine(target, "one.mp4")); File.Copy(Path.Combine(target, "one.mp4"), Path.Combine(target, "two.mp4"));
        var plan = await IndexRepairService.BuildAsync([], [target], videos: _videos.GetAll());
        Assert.Equal(2, plan.Operations.Count); Assert.True(plan.Operations[0].AddReference);
        foreach (var operation in plan.Operations)
            Assert.NotEqual("failed", IndexRepairService.Apply(_db, operation, await IndexRepairService.VerifyAsync(operation)).Status);
        var rows = _videos.GetAll(); Assert.Equal(2, rows.Count);
        Assert.All(rows, row => { Assert.Equal(video.Sha256, row.Sha256); Assert.True(_videos.HasCurrentSummary(row)); Assert.Equal("preserved transcript", _videos.GetTranscript(row.Id)); });
        Assert.Equal(1m, new Picer.Core.Vision.CloudVisionRepository(_db).GetEstimatedCostSince(DateTime.MinValue));
        Assert.Single(rows.Select(row => _videos.GetContentId(row.Id)).Distinct());
        foreach (var table in new[] { "video_summaries", "video_transcripts", "video_keyframes", "video_face_person_rejections", "video_tags", "video_visual_concepts" })
        {
            using var count = _db.Command($"SELECT COUNT(*) FROM {table}"); Assert.Equal(1L, count.ExecuteScalar());
        }
        using var faces = _db.Command("SELECT COUNT(*) FROM video_detected_faces WHERE assignment_state='ignored'"); Assert.Equal(1L, faces.ExecuteScalar());
        using var confirmed = _db.Command("SELECT COUNT(*) FROM video_detected_faces WHERE assignment_state='confirmed' AND assigned_person_id IS NOT NULL"); Assert.Equal(1L, confirmed.ExecuteScalar());
        using var foreignKeys = _db.Command("PRAGMA foreign_key_check"); Assert.Null(foreignKeys.ExecuteScalar());
    }

    [Fact]
    public async Task MissingLegacyVideoWithoutHashCannotBeMatchedByNameOrSize()
    {
        var video = Seed(); using (var clear = _db.Command("UPDATE videos SET sha256=NULL")) clear.ExecuteNonQuery();
        var target = Path.Combine(_root, "new"); Directory.CreateDirectory(target); File.Move(video.Path, Path.Combine(target, "clip.mp4"));
        var operation = Assert.Single((await IndexRepairService.BuildAsync([], [target], videos: _videos.GetAll())).Operations);
        Assert.Null(operation.NewPath);
        Assert.Equal("missing", IndexRepairService.Apply(_db, operation, await IndexRepairService.VerifyAsync(operation)).Status);
        Assert.Equal("preserved", _videos.GetSummary(video.Id)); Assert.Equal("missing", Assert.Single(_videos.GetAll()).Status);
    }

    [Fact]
    public async Task ChangedVideoTargetIsRejectedAndHashSurvivesMirror()
    {
        var video = Seed(); var target = Path.Combine(_root, "new"); Directory.CreateDirectory(target);
        var newPath = Path.Combine(target, "clip.mp4"); File.Move(video.Path, newPath);
        var operation = Assert.Single((await IndexRepairService.BuildAsync([], [target], videos: _videos.GetAll())).Operations);
        File.WriteAllBytes(newPath, [3, 2, 1]);
        Assert.Equal("hash_mismatch", IndexRepairService.Apply(_db, operation, await IndexRepairService.VerifyAsync(operation)).Error);
        using var mirror = new PicerDatabase(Path.Combine(_root, "mirror.db"));
        var mirrored = new VideoRepository(mirror); mirrored.UpsertMirroredBatch([video], mirror.AddSource(Path.GetDirectoryName(video.Path)!).Id);
        Assert.Equal(video.Sha256, Assert.Single(mirrored.GetAll()).Sha256);
    }

    [Fact]
    public void SameHashMirrorRebindPreservesSummaryButChangedHashInvalidatesIt()
    {
        var video = Seed();
        _videos.UpsertMirrored(video with { Path = video.Path + ".moved.mp4", ModifiedUtc = video.ModifiedUtc.AddMinutes(2) }, video.SourceId);
        var moved = Assert.Single(_videos.GetAll());
        Assert.True(_videos.HasCurrentSummary(moved)); Assert.Equal("preserved transcript", _videos.GetTranscript(video.Id));
        _videos.UpsertMirrored(moved with { Sha256 = new string('a', 64) }, moved.SourceId);
        Assert.Null(_videos.GetSummary(video.Id)); Assert.Null(_videos.GetTranscript(video.Id));
    }

    public void Dispose()
    {
        _db.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true);
    }
}
