using Picer.Core.Data;
using Picer.Core.Imaging;
using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class DirectoryScanBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-boundaries-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly long _source;
    public DirectoryScanBoundaryTests() { _db = new PicerDatabase(Path.Combine(_root, "picer.db")); _source = _db.AddSource(_root).Id; }

    private ImageRecord Add(string file) => _db.UpsertImage(new() { SourceId = _source, Path = Path.Combine(_root, file), FileSize = 1, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow });

    [Fact]
    public async Task CorruptFileDoesNotAbortScanOrBecomeMissingAndRealPhotoGetsMetadata()
    {
        var bad = Add("bad.jpg"); var missing = Add("gone.jpg");
        File.WriteAllBytes(bad.Path, [1,2,3]);
        using (var image = new RasterImage(24, 12, new RgbPixel(25, 80, 120))) image.SaveAsPng(Path.Combine(_root, "good.png"));
        var page = await new DirectoryMediaScanner().ReadAsync(new(_source, _root));
        Assert.True(page.Done); Assert.Equal(2, page.NextOffset);
        Assert.Single(page.Failures!); Assert.Single(page.Items);
        DirectoryMediaImport.Apply(_db, _source, _root, page);
        Assert.Equal(ImageStatus.Active, _db.GetImage(bad.Id)!.Status);
        Assert.Equal(ImageStatus.Missing, _db.GetImage(missing.Id)!.Status);
        var good = _db.GetImages().Single(image => image.Path.EndsWith("good.png"));
        Assert.Equal(24, good.Width); Assert.Equal(12, good.Height); Assert.Equal(64, good.Sha256!.Length);
    }

    [Fact]
    public void IncompleteOrOutOfOrderScanNeverMarksMissing()
    {
        var record = Add("gone.jpg");
        DirectoryMediaImport.Apply(_db, _source, _root, new("partial", 2, 1, false, [], 0, [Path.Combine(_root,"observed.jpg")], [], true));
        Assert.Equal(ImageStatus.Active, _db.GetImage(record.Id)!.Status);
        DirectoryMediaImport.Apply(_db, _source, _root, new("partial", 2, 2, true, [], 2, [], [], true));
        Assert.Equal(ImageStatus.Active, _db.GetImage(record.Id)!.Status);
        DirectoryMediaImport.Apply(_db, _source, _root, new("inaccessible", 0, 0, true, [], 0, [], [new(_root, "denied", true)], false));
        Assert.Equal(ImageStatus.Active, _db.GetImage(record.Id)!.Status);
    }

    [Fact]
    public void ChangedVideoClearsDerivedResultsAndOldMetadata()
    {
        var repository = new VideoRepository(_db);
        var path = Path.Combine(_root, "clip.mp4");
        repository.UpsertRemote(new(0, _source, path, 10, DateTime.UtcNow.AddDays(-1), 5, 640, 480, DateTime.UtcNow, "10,20", "active"), _source);
        var video = Assert.Single(repository.GetAll());
        using (var command = _db.Command("""
            INSERT INTO video_summaries(video_id,source_file_size,source_modified_utc,provider,model_version,prompt_version,summary,result_json,created_utc)
            VALUES($id,10,'','test','v1','v1','old description','{}','2026-10-09');
            UPDATE videos SET person_analysis_status='completed',person_analyzed_utc='2026-10-09' WHERE id=$id;
            """)) { command.Parameters.AddWithValue("$id", video.Id); command.ExecuteNonQuery(); }
        DirectoryMediaImport.Apply(_db, _source, _root, new("video", 1, 1, true,
            [new(path, 20, DateTime.UtcNow, true, null, new(1920,1080, DurationSeconds: 8))]));
        var changed = Assert.Single(repository.GetAll());
        Assert.Equal(video.Id, changed.Id); Assert.Equal(1920, changed.Width); Assert.Equal(8, changed.DurationSeconds);
        Assert.Null(changed.Gps); Assert.Null(changed.TakenUtc); Assert.Null(changed.PersonAnalysisStatus);
        using var count = _db.Command("SELECT COUNT(*) FROM video_summaries"); Assert.Equal(0L, count.ExecuteScalar());
    }

    [Fact]
    public void MirroredVideoChangeCannotRetainOldSummaryOrPersonState()
    {
        var repository = new VideoRepository(_db);
        var video = new VideoRecord(42, _source, Path.Combine(_root,"clip.mp4"), 10, DateTime.UtcNow, 5, 640, 480, null, null, "active")
            { PersonAnalysisStatus = "completed", PersonAnalyzedUtc = "2026-10-09" };
        repository.UpsertMirrored(video, _source);
        using (var command = _db.Command("""
            INSERT INTO video_summaries(video_id,source_file_size,source_modified_utc,provider,model_version,prompt_version,summary,result_json,created_utc)
            VALUES((SELECT content_id FROM videos WHERE id=42),10,'','test','v1','v1','old description','{}','2026-10-09');
            """)) command.ExecuteNonQuery();
        repository.UpsertMirrored(video with { FileSize = 20, PersonAnalysisStatus = null, PersonAnalyzedUtc = null }, _source);
        Assert.Null(Assert.Single(repository.GetAll()).PersonAnalysisStatus);
        using var count = _db.Command("SELECT COUNT(*) FROM video_summaries"); Assert.Equal(0L, count.ExecuteScalar());
    }

    [Fact]
    public void FfmpegFallbackExtractsDateAndLocation()
    {
        var probe = FfmpegKeyframeService.ParseFfmpegProbeOutput("""
              creation_time : 2026-01-02T03:04:05Z
              location : +31.2304+121.4737/
              Duration: 00:00:08.00, start: 0.000000
              Stream #0:0: Video: h264, yuv420p, 1920x1080, 30 fps
            """);
        Assert.Equal(new DateTime(2026,1,2,3,4,5,DateTimeKind.Utc), probe.TakenUtc);
        Assert.Equal("+31.2304,+121.4737", probe.Gps);
    }

    public void Dispose() { _db.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
