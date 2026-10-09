using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Video;
using Picer.Core.Vision;
using Picer.Core.Gateway;
using System.Net;

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
        Assert.Equal((long)PicerDatabase.CurrentSchemaVersion, (long)version.ExecuteScalar()!);
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('videos','video_keyframes','video_transcripts')";
        Assert.Equal(3L, (long)tables.ExecuteScalar()!);
    }

    [Fact]
    public void FaceReviewSummary_ReportsIndependentTranscriptAndSummaryFlags()
    {
        var source = _database.AddSource(_root);
        var path = Path.Combine(_root, "analysis-flags.mp4");
        File.WriteAllBytes(path, [1, 2, 3]);
        var repository = new VideoRepository(_database);
        var video = repository.Upsert(source.Id, path);
        repository.SavePersonAnalysisStatus(video.Id, "completed");
        repository.SaveTranscript(video.Id, "test-asr", "{\"text\":\"hello\"}");

        var transcriptOnly = repository.GetFaceReviewSummaries()[video.Id];
        Assert.Equal("completed", transcriptOnly.AnalysisStatus);
        Assert.True(transcriptOnly.HasTranscript);
        Assert.False(transcriptOnly.HasSummary);

        using var command = _database.Command("""
            INSERT INTO video_summaries(video_id,provider,model_version,prompt_version,source_file_size,
                source_modified_utc,summary,result_json,created_utc)
            VALUES($video,'test','test','test',0,'','summary','{}',$now)
            """);
        command.Parameters.AddWithValue("$video", video.Id);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        command.ExecuteNonQuery();

        var completed = repository.GetFaceReviewSummaries()[video.Id];
        Assert.True(completed.HasTranscript);
        Assert.True(completed.HasSummary);
    }

    [Fact]
    public void RemoteVideoCatalogBatchRefresh_PreservesKeyframesTranscriptPeopleAndSummary()
    {
        var source = _database.AddSource(_root);
        var path = Path.Combine(_root, "mirrored.mp4");
        var framePath = Path.Combine(_root, "frame.jpg");
        File.WriteAllBytes(path, [1, 2, 3]);
        File.WriteAllBytes(framePath, [4, 5, 6]);
        var repository = new VideoRepository(_database);
        var original = repository.Upsert(source.Id, path);
        repository.SaveKeyframes(original.Id, [new VideoKeyframe(1.25, framePath)]);
        repository.SavePersonAnalysisStatus(original.Id, "completed");
        repository.SaveTranscript(original.Id, "asr-v1", "{\"text\":\"spoken words\"}");
        using (var summary = _database.Command("""
                   INSERT INTO video_summaries(video_id,provider,model_version,prompt_version,source_file_size,
                       source_modified_utc,summary,result_json,created_utc)
                   VALUES($video,'test','vl-v1','prompt-v1',3,'','family picnic','{\"summary\":\"family picnic\"}',$now)
                   """))
        {
            summary.Parameters.AddWithValue("$video", original.Id);
            summary.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            summary.ExecuteNonQuery();
        }

        var remoteRefresh = original with
        {
            Path = Path.Combine(_root, "mirrored-from-service.mp4"),
            // A path/probe refresh preserves derivatives only when the content
            // signature is unchanged. Changed sizes are covered by invalidation tests.
            FileSize = original.FileSize,
            DurationSeconds = 12.5,
            Width = 1280,
            Height = 720
        };
        repository.UpsertMirroredBatch([remoteRefresh], source.Id);
        repository.UpsertMirroredBatch([remoteRefresh], source.Id); // retry must be idempotent

        var video = Assert.Single(repository.GetAll());
        var keyframe = Assert.Single(repository.GetKeyframes(original.Id, existingOnly: false));
        var review = Assert.Single(repository.GetFaceReviewSummaries()).Value;
        Assert.Equal(remoteRefresh.Path, video.Path);
        Assert.Equal(1.25, keyframe.TimestampSeconds);
        Assert.Equal("{\"text\":\"spoken words\"}", repository.GetTranscript(original.Id));
        Assert.Equal("family picnic", repository.GetSummary(original.Id));
        Assert.Equal("completed", review.AnalysisStatus);
        Assert.True(review.HasTranscript);
        Assert.True(review.HasSummary);
    }

    [Theory]
    [InlineData("a.mp4")][InlineData("a.MOV")][InlineData("a.m2ts")]
    public void VideoFormats_AcceptsCommonFamilyVideoFormats(string path) => Assert.True(VideoFormats.IsSupported(path));

    [Fact]
    public void VideoMetadataMirror_PreservesModelNamespaceAndPreviouslyDownloadedVectors()
    {
        var source = _database.AddSource(_root);
        var path = Path.Combine(_root, "namespace.mp4");
        File.WriteAllBytes(path, [1]);
        var repository = new VideoRepository(_database);
        var video = repository.Upsert(source.Id, path);
        var now = DateTime.UtcNow.ToString("O");
        var face = new GatewayVideoFace(10, video.Id, 20, 1, 0, .1, .1, .5, .5, .99,
            [1, 2, 3, 4], null, null, null, "ignored", now, now) { ModelVersion = "insightface-arcface" };
        var metadata = new GatewayMetadata([], [], [], [], [],
            [new GatewayVideoKeyframe(20, video.Id, 1, "frame.jpg", 0)], [], [], [face]);
        _database.UpsertMirroredVideoMetadata(metadata);
        _database.UpsertMirroredVideoMetadata(metadata with { VideoFaces = [face with { Embedding = null }] });
        var mirrored = Assert.Single(_database.GetAllMirroredVideoFaces());
        Assert.Equal("insightface-arcface", mirrored.ModelVersion);
        Assert.Equal(face.Embedding, mirrored.Embedding);
        Assert.Equal("ignored", mirrored.AssignmentState);
        // A legacy server omitting the optional model field cannot erase it.
        _database.UpsertMirroredVideoMetadata(metadata with { VideoFaces = [face with { ModelVersion = "", Embedding = null }] });
        Assert.Equal("insightface-arcface", Assert.Single(_database.GetAllMirroredVideoFaces()).ModelVersion);
    }

    [Fact]
    public void RemoteVideoWithoutFaces_StillRetainsCompletedAnalysisStatus()
    {
        var source = _database.AddSource(_root);
        var repository = new VideoRepository(_database);
        var record = new VideoRecord(100, source.Id, Path.Combine(_root, "empty.mp4"), 0, DateTime.UtcNow,
            null, 0, 0, null, null, "active")
        { PersonAnalysisStatus = "completed", PersonAnalyzedUtc = DateTime.UtcNow.ToString("O") };
        repository.UpsertMirroredBatch([record], source.Id);
        Assert.Equal("completed", Assert.Single(repository.GetAll()).PersonAnalysisStatus);
        Assert.Equal("completed", Assert.Single(repository.GetFaceReviewSummaries()).Value.AnalysisStatus);
        // Old servers have no status fields: a catalog refresh must not reset them.
        repository.UpsertMirrored(record with { PersonAnalysisStatus = null, PersonAnalyzedUtc = null }, source.Id);
        Assert.Equal("completed", Assert.Single(repository.GetAll()).PersonAnalysisStatus);
    }

    [Fact]
    public void MirroredImageBatch_IsIdempotentAndPreservesIdentifiers()
    {
        var source = _database.AddSource(_root);
        var records = Enumerable.Range(1, 50).Select(id => new ImageRecord
        {
            Id = id, SourceId = source.Id, Path = Path.Combine(_root, $"{id}.jpg"),
            ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow
        }).ToArray();
        _database.UpsertMirroredImageBatch(records);
        var changes = Convert.ToInt64(_database.Command("SELECT total_changes()").ExecuteScalar());
        _database.UpsertMirroredImageBatch(records);
        Assert.Equal(changes, Convert.ToInt64(_database.Command("SELECT total_changes()").ExecuteScalar()));
        Assert.Equal(50, _database.GetImages().Count);
        Assert.Equal(records.Select(record => record.Id).Order(), _database.GetImages().Select(record => record.Id).Order());
    }

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
    public async Task KeyframeExtractionFailure_PreservesLastKnownGoodFrames()
    {
        var cache = Path.Combine(_root, "keyframes");
        var directory = Path.Combine(cache, "7");
        Directory.CreateDirectory(directory);
        var existing = Path.Combine(directory, "scene-00001.jpg");
        await File.WriteAllBytesAsync(existing, [1, 2, 3]);
        var video = new VideoRecord(7, 1, Path.Combine(_root, "missing.mp4"), 0, DateTime.UtcNow,
            null, 0, 0, null, null, "active");
        var service = new FfmpegKeyframeService(Path.Combine(_root, "missing-ffmpeg.exe"), cache);

        await Assert.ThrowsAnyAsync<Exception>(() => service.ExtractAsync(video));

        Assert.True(File.Exists(existing));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(existing));
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
        var trainingFace = Assert.Single(new VisionAnalysisRepository(_database).GetVideoTrainingFaces());
        Assert.Equal(video.Id, trainingFace.VideoId);
        Assert.Equal(person.Id, trainingFace.PersonId);
        Assert.Equal("positive", trainingFace.Label);
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

    [Fact]
    public async Task VideoTimeline_PrefersConfiguredSpaceAndFallsBackOnlyWhenItIsEmpty()
    {
        var source = _database.AddSource(_root);
        var videoPath = Path.Combine(_root, "model-fallback.mp4");
        File.WriteAllBytes(videoPath, [1, 2, 3]);
        var videos = new VideoRepository(_database);
        var video = videos.Upsert(source.Id, videoPath);
        var frames = Enumerable.Range(0, 2).Select(index =>
        {
            var path = Path.Combine(_root, $"model-fallback-{index}.jpg");
            File.WriteAllBytes(path, [4, 5, (byte)index]);
            return new VideoKeyframe(index + 1, path);
        }).ToArray();
        videos.SaveKeyframes(video.Id, frames);
        var repository = new VisionAnalysisRepository(_database);
        var fallback = new VideoFaceTimelineService(_database, new StubFaceAnalyzer("sface-fallback", faceCount: 5), repository);
        var preferredFallback = new VideoFaceTimelineService(_database, new StubFaceAnalyzer("adaface-fallback", faceCount: 1), repository);
        var preferred = new VideoFaceTimelineService(_database, new StubFaceAnalyzer("insightface-default"), repository);

        await fallback.AnalyzeAsync(video);
        await preferredFallback.AnalyzeAsync(video);
        var fallbackRows = preferred.GetTimeline(video.Id);
        Assert.Equal(2, fallbackRows.Count);
        Assert.All(fallbackRows, face => Assert.Equal("adaface-fallback", face.ModelVersion));

        await preferred.AnalyzeAsync(video);
        var preferredRows = preferred.GetTimeline(video.Id);
        Assert.Equal(2, preferredRows.Count);
        Assert.All(preferredRows, face => Assert.Equal("insightface-default", face.ModelVersion));
    }

    private sealed class StubFaceAnalyzer : IImageFaceAnalyzer
    {
        private readonly int _faceCount;
        public StubFaceAnalyzer(string modelVersion = "stub-v1", int faceCount = 1)
        {
            ModelVersion = modelVersion;
            _faceCount = faceCount;
        }
        public string AnalyzerName => "stub";
        public string ModelVersion { get; }
        public Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken) =>
            Task.FromResult(new FaceAnalysisResult(Enumerable.Range(0, _faceCount)
                .Select(index => new FaceDetection(new NormalizedFaceBox(.1f + index * .02f, .1f, .3f, .3f), .9f,
                    Embedding: [1f, 0f, 0f])).ToArray()));
    }

    [Fact]
    public async Task QwenAsr_UsesConfiguredMultipartEndpointAndReturnsJson()
    {
        var audioDirectory = Path.Combine(_root, "audio", "42");
        Directory.CreateDirectory(audioDirectory);
        await File.WriteAllBytesAsync(Path.Combine(audioDirectory, "audio-16k.wav"), [1, 2, 3, 4]);
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        using var service = new QwenAsrTranscriptionService("unused-ffmpeg",
            new Uri("http://127.0.0.1:8090/api/asr/transcriptions"), "qwen3-asr-1.7b",
            Path.Combine(_root, "audio"), client);
        var video = new VideoRecord(42, 1, Path.Combine(_root, "video.mp4"), 1, DateTime.UtcNow,
            null, 0, 0, null, null, "active");

        var result = await service.TranscribeAsync(video);

        Assert.Equal("{\"text\":\"你好\"}", result);
        Assert.Contains("qwen3-asr-1.7b", handler.Body);
        Assert.Contains("audio-16k.wav", handler.Body);
        Assert.Contains("vad_mode", handler.Body);
        Assert.Contains("full_audio_fallback", handler.Body);
        Assert.Contains("timestamps", handler.Body);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"你好\"}")
            };
        }
    }

    public void Dispose() { _database.Dispose(); try { Directory.Delete(_root, true); } catch { } }
}
