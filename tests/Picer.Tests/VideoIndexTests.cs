using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class VideoIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-video-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;

    public VideoIndexTests()
    {
        Directory.CreateDirectory(_root);
        _database = new PicerDatabase(Path.Combine(_root, "video.db"));
    }

    [Fact]
    public async Task VideoIndex_FindsSupportedFilesWithoutModifyingThem()
    {
        _database.AddSource(_root);
        var video = Path.Combine(_root, "family.MP4");
        File.WriteAllBytes(video, [1, 2, 3, 4]);
        var before = File.ReadAllBytes(video);
        var repository = new VideoRepository(_database);

        var count = await new VideoIndexService(_database, repository).ScanLocalSourcesAsync();

        Assert.Equal(1, count);
        Assert.Equal(video, Assert.Single(repository.GetAll()).Path);
        Assert.Equal(before, File.ReadAllBytes(video));
        var record = Assert.Single(repository.GetAll());
        repository.SaveTranscript(record.Id, "base-q5", "{\"text\":\"孩子在九寨沟唱歌\"}");
        Assert.Equal(record.Id, Assert.Single(repository.Search("唱歌")).Id);
    }

    [Fact]
    public void Database_HasVersionNineVideoTables()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "video.db")}");
        connection.Open();
        using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version";
        Assert.Equal(24L, (long)version.ExecuteScalar()!);
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('videos','video_keyframes','video_transcripts')";
        Assert.Equal(3L, (long)tables.ExecuteScalar()!);
    }

    [Theory]
    [InlineData("a.mp4")][InlineData("a.MOV")][InlineData("a.m2ts")]
    public void VideoFormats_AcceptsCommonFamilyVideoFormats(string path) => Assert.True(VideoFormats.IsSupported(path));

    [Fact]
    public void FfmpegProbeFallback_ParsesDurationAndResolution()
    {
        const string output = "Duration: 00:01:23.45, start: 0.000000, bitrate: 1000 kb/s\n" +
                              "Stream #0:0: Video: h264, yuv420p(progressive), 1920x1080, 30 fps";

        var result = FfmpegKeyframeService.ParseFfmpegProbeOutput(output);

        Assert.Equal(83.45, result.DurationSeconds, 2);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
    }

    [Fact]
    public async Task VideoFaceIdentity_SurvivesReanalysisAndContributesToSharedPersonSummary()
    {
        var source = _database.AddSource(_root);
        var videoPath = Path.Combine(_root, "identity.mp4");
        var keyframePath = Path.Combine(_root, "frame.jpg");
        File.WriteAllBytes(videoPath, [1, 2, 3]);
        File.WriteAllBytes(keyframePath, [4, 5, 6]);
        var videos = new VideoRepository(_database);
        var video = videos.Upsert(source.Id, videoPath);
        videos.SaveKeyframes(video.Id, [new VideoKeyframe(2.5, keyframePath)]);
        var people = new PeopleService(_database);
        var person = people.Create("视频人物");
        var service = new VideoFaceTimelineService(_database, new StubFaceAnalyzer(), new VisionAnalysisRepository(_database));

        await service.AnalyzeAsync(video);
        var detected = Assert.Single(service.GetTimeline(video.Id));
        var pending = videos.GetFaceReviewSummaries()[video.Id];
        Assert.Equal(1, pending.Total);
        Assert.Equal(1, pending.Pending);
        service.Confirm(detected.FaceId, person.Id);
        await service.AnalyzeAsync(video);

        var reanalyzed = Assert.Single(service.GetTimeline(video.Id));
        Assert.Equal(person.Id, reanalyzed.AssignedPersonId);
        Assert.Equal("confirmed", reanalyzed.AssignmentState);
        var summary = Assert.Single(people.GetAll());
        Assert.Equal(1, summary.ConfirmedFaceCount);
        Assert.Equal(1, summary.PhotoCount);

        service.MarkIgnored(reanalyzed.FaceId);
        Assert.Equal("ignored", Assert.Single(service.GetTimeline(video.Id)).AssignmentState);
        var ignored = videos.GetFaceReviewSummaries()[video.Id];
        Assert.Equal(0, ignored.Pending);
        Assert.Equal(1, ignored.Ignored);

        var emptyVideoPath = Path.Combine(_root, "no-faces.mp4");
        File.WriteAllBytes(emptyVideoPath, [7, 8, 9]);
        var emptyVideo = videos.Upsert(source.Id, emptyVideoPath);
        videos.SavePersonAnalysisStatus(emptyVideo.Id, "completed");
        var noFaces = videos.GetFaceReviewSummaries()[emptyVideo.Id];
        Assert.Equal(0, noFaces.Total);
        Assert.Equal("completed", noFaces.AnalysisStatus);
    }

    private sealed class StubFaceAnalyzer : IImageFaceAnalyzer
    {
        public string AnalyzerName => "stub";
        public string ModelVersion => "stub-v1";
        public Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken) =>
            Task.FromResult(new FaceAnalysisResult([new FaceDetection(new NormalizedFaceBox(.1f, .1f, .3f, .3f), .9f,
                Embedding: [1f, 0f, 0f])]));
    }

    public void Dispose() { _database.Dispose(); try { Directory.Delete(_root, true); } catch { } }
}
