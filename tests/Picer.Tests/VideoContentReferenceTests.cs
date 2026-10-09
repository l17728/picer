using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Search;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class VideoContentReferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "video-content-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly VideoRepository _videos;
    private readonly long _source;
    public VideoContentReferenceTests()
    {
        _db = new(Path.Combine(_root, "test.db"));
        _videos = new(_db);
        _source = _db.AddSource(_root).Id;
    }

    private VideoRecord Add(long id, string? hash = null)
    {
        var record = new VideoRecord(id, _source, Path.Combine(_root, id + ".mp4"), 3,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, 0, 0, null, null, "active")
        { Sha256 = hash ?? new string('a', 64) };
        _videos.UpsertMirrored(record, _source);
        return record;
    }

    private long Count(string table)
    { using var command = _db.Command("SELECT COUNT(*) FROM " + table); return (long)command.ExecuteScalar()!; }

    private void Delete(long id)
    { using var command = _db.Command("DELETE FROM videos WHERE id=$id"); command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery(); }

    [Fact]
    public void CopiesShareEvidenceAndBm25ButReferencesRemainIndependent()
    {
        Add(100); Add(200);
        _videos.SaveTranscript(100, "test", "unique searchable elephant");
        _videos.SaveKeyframes(100, [new(0, "cached.jpg", 0)]);
        new VideoSearchMetadata(_db).SetConcept(200, "elephant", "manual");
        Assert.Equal(_videos.GetContentId(100), _videos.GetContentId(200));
        Assert.Equal("unique searchable elephant", _videos.GetTranscript(200));
        Assert.Single(_videos.GetKeyframes(200, false));
        Assert.Contains("elephant", new VideoSearchMetadata(_db).GetConcepts(100));
        Assert.Equal(1, Count("video_transcripts")); Assert.Equal(1, Count("video_keyframes"));
        Assert.Equal(1, Count("video_contents")); Assert.Equal(1, Count("media_text_documents"));
        var ranked = new MediaTextRanking(_db).Search(["elephant"]);
        Assert.Contains(-100L, ranked.Keys); Assert.Contains(-200L, ranked.Keys);
    }

    [Fact]
    public void DeletingRepresentativeRetainsEvidenceAndLastReferenceRemovesIt()
    {
        Add(100); Add(200); _videos.SaveTranscript(100, "test", "retained");
        var content = _videos.GetContentId(100);
        Delete(100);
        Assert.Equal(content, _videos.GetContentId(200));
        Assert.Equal("retained", _videos.GetTranscript(200));
        Assert.Equal(200, Assert.Single(_db.GetAllMirroredVideoTranscripts()).VideoId);
        Assert.Throws<InvalidOperationException>(() => _videos.SaveTranscript(100, "test", "stale"));
        Delete(200);
        Assert.Equal(0, Count("video_contents")); Assert.Equal(0, Count("video_transcripts"));
        Assert.Equal(0, Count("media_text_documents"));
    }

    [Fact]
    public void ChangedReferenceDetachesWithoutErasingOtherCopies()
    {
        var changed = Add(100); Add(200); _videos.SaveTranscript(100, "test", "old evidence");
        _videos.UpsertMirrored(changed with { Sha256 = new string('b', 64) }, _source);
        Assert.NotEqual(_videos.GetContentId(100), _videos.GetContentId(200));
        Assert.Null(_videos.GetTranscript(100)); Assert.Equal("old evidence", _videos.GetTranscript(200));
        Delete(200); Assert.Equal(1, Count("video_contents")); Assert.Equal(0, Count("video_transcripts"));
    }

    [Fact]
    public void DiscoveringLegacyHashTransfersEvidenceToExistingContent()
    {
        var legacy = Add(100, ""); Add(200);
        _videos.SaveTranscript(100, "test", "legacy evidence");
        _videos.UpsertMirrored(legacy with { Sha256 = new string('a', 64) }, _source);
        Assert.Equal(_videos.GetContentId(100), _videos.GetContentId(200));
        Assert.Equal("legacy evidence", _videos.GetTranscript(200));
        Assert.Equal(1, Count("video_contents"));
        using var check = _db.Command("PRAGMA foreign_key_check"); Assert.Null(check.ExecuteScalar());
    }

    [Fact]
    public void AuthoritativeMirrorMapsSharedContentAndRemovesAbsentEvidence()
    {
        Add(100); Add(200);
        _db.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], [], [], [],
            [new(99, 200, 0, "frame.jpg", 0)], [new(200, "test", "mirror text")], []), true);
        Assert.Equal("mirror text", _videos.GetTranscript(100));
        Assert.Single(_videos.GetKeyframes(100, false));
        _db.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], [], [], [], [], [], []), true);
        Assert.Null(_videos.GetTranscript(100)); Assert.Empty(_videos.GetKeyframes(100, false));
    }

    [Fact]
    public void LateSummaryForReplacedContentOnlyRecordsActualRequestCost()
    {
        var original = Add(100);
        _videos.UpsertMirrored(original with { Sha256 = new string('b', 64) }, _source);
        _videos.SaveSummary(100, new Analyzer(), original,
            new CloudVisionResult("old result", [], [], [], [], Usage: new CloudVisionUsage(1, 1, .5m)), "late-request");
        Assert.Null(_videos.GetSummary(100));
        Assert.False(_videos.HasCurrentSummary(original));
        Assert.Equal(1, Count("analysis_billing_events"));
        Assert.Equal(0, Count("video_summaries"));
    }

    private sealed class Analyzer : ICloudVisionAnalyzer
    {
        public string Provider => "test";
        public string ModelVersion => "test";
        public string PromptVersion => "test";
        public Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void LateProbeTranscriptAndFramesRejectChangedReferenceAtomically()
    {
        var original = Add(100);
        _videos.UpsertMirrored(original with { Sha256 = new string('b', 64) }, _source);
        Assert.Throws<InvalidOperationException>(() => _videos.SaveProbe(100, new(60, 1920, 1080), original));
        Assert.Throws<InvalidOperationException>(() => _videos.SaveTranscript(100, "test", "stale", original));
        Assert.Throws<InvalidOperationException>(() => _videos.SaveKeyframes(100, [new(0, "stale.jpg", 0)], original));
        Assert.Null(_videos.Get(100)!.DurationSeconds);
        Assert.Null(_videos.GetTranscript(100)); Assert.Empty(_videos.GetKeyframes(100, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShaCaseOnlyRefreshPreservesContentSummaryAndManualReview(bool mirrored)
    {
        var original = Add(100, new string('A', 64));
        Assert.Equal(new string('a', 64), _videos.Get(100)!.Sha256);
        _videos.SaveSummary(100, new Analyzer(), original, new("preserved", [], [], [], []));
        _videos.SaveKeyframes(100, [new(0, "reviewed.jpg", 0)]);
        _videos.SavePersonAnalysisStatus(100, "completed");
        var content = _videos.GetContentId(100);
        using (var seed = _db.Command("""
            UPDATE videos SET sha256=upper(sha256) WHERE id=100;
            INSERT INTO video_detected_faces(video_id,keyframe_id,timestamp_seconds,ordinal,model_version,
                box_x,box_y,box_width,box_height,confidence,assignment_state,created_utc)
            SELECT video_id,id,0,0,'test',0,0,1,1,1,'ignored','2026-01-01' FROM video_keyframes;
            """)) seed.ExecuteNonQuery();
        var changed = original with { Sha256 = new string('a', 64), ModifiedUtc = original.ModifiedUtc.AddMinutes(5) };
        if (mirrored) _videos.UpsertMirrored(changed, _source);
        else _videos.UpsertRemote(changed, _source);
        Assert.Equal(content, _videos.GetContentId(100));
        Assert.Equal("preserved", _videos.GetSummary(100));
        Assert.True(_videos.HasCurrentSummary(changed));
        Assert.Equal("completed", _videos.Get(100)!.PersonAnalysisStatus);
        Assert.Equal(new string('a', 64), _videos.Get(100)!.Sha256);
        using var review = _db.Command("SELECT assignment_state FROM video_detected_faces");
        Assert.Equal("ignored", review.ExecuteScalar());
        Add(200, new string('A', 64));
        Assert.Equal(content, _videos.GetContentId(200));
        Assert.Equal(1, Count("video_contents")); Assert.Equal(1, Count("video_summaries"));
    }

    public void Dispose()
    { _db.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
