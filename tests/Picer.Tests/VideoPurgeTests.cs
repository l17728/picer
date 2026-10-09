using Picer.Core.Data;
using Picer.Core.Video;
using Picer.IndexService;

namespace Picer.Tests;

public sealed class VideoPurgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-video-purge-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NonLastRemoteReferenceClearsItsDownloadsWithoutTouchingSameIdLocalCache()
    {
        using var db = new PicerDatabase(Path.Combine(_root, "mirror.db"));
        var source = db.AddSource(_root); var videos = new VideoRepository(db);
        var a = Path.Combine(_root, "a.mp4"); var b = Path.Combine(_root, "b.mp4");
        File.WriteAllBytes(a, [1]); File.WriteAllBytes(b, [1]);
        var first = videos.Upsert(source.Id, a); var second = videos.Upsert(source.Id, b);
        var local = Path.Combine(_root, "video-keyframes", first.Id.ToString());
        var remote = Path.Combine(_root, "remote-video-keyframes", first.Id.ToString());
        Directory.CreateDirectory(local); Directory.CreateDirectory(remote);
        File.WriteAllBytes(Path.Combine(local, "poster.jpg"), [2]); File.WriteAllBytes(Path.Combine(remote, "frame.jpg"), [3]);
        var sourceCache = VideoRecordPurger.SourceCacheDirectory(a); Directory.CreateDirectory(sourceCache);
        File.WriteAllBytes(Path.Combine(sourceCache, "frame.jpg"), [4]);
        Assert.True(new VideoRecordPurger(db, VideoPurgeCacheScope.Remote).Purge(first.Id));
        Assert.False(Directory.Exists(remote)); Assert.True(Directory.Exists(local)); Assert.True(Directory.Exists(sourceCache));
        Assert.Equal(second.Id, Assert.Single(videos.GetAll()).Id);
    }

    [Fact]
    public void PurgingReplacementContentPreservesOldGenerationUsedByAnotherContent()
    {
        using var db = new PicerDatabase(Path.Combine(_root, "picer.db"));
        var source = db.AddSource(_root); var videos = new VideoRepository(db);
        var a = Path.Combine(_root, "a.mp4"); var b = Path.Combine(_root, "b.mp4");
        File.WriteAllBytes(a, [1]); File.WriteAllBytes(b, [1]);
        var first = videos.Upsert(source.Id, a); var second = videos.Upsert(source.Id, b);
        var cache = Path.Combine(_root, "video-keyframes", first.Id.ToString());
        var old = Path.Combine(cache, "generation-old", "frame.jpg"); Directory.CreateDirectory(Path.GetDirectoryName(old)!); File.WriteAllBytes(old, [7]);
        videos.SaveKeyframes(first.Id, [new VideoKeyframe(0, old)]);
        File.WriteAllBytes(a, [2, 3]); first = videos.Upsert(source.Id, a);
        var fresh = Path.Combine(cache, "generation-new", "frame.jpg"); Directory.CreateDirectory(Path.GetDirectoryName(fresh)!); File.WriteAllBytes(fresh, [8]);
        videos.SaveKeyframes(first.Id, [new VideoKeyframe(0, fresh)]);
        Assert.True(new VideoRecordPurger(db).Purge(first.Id));
        Assert.True(File.Exists(old)); Assert.False(File.Exists(fresh));
        Assert.Equal(old, Assert.Single(videos.GetKeyframes(second.Id)).Path);
        Assert.True(new VideoRecordPurger(db).Purge(second.Id)); Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public void RemoteGenerationPublicationDoesNotIncludeOldTailFramesAndAbortLeavesNoStaging()
    {
        var cache = Path.Combine(_root, "source-cache");
        string old;
        using (var generation = new VideoKeyframeGeneration(cache))
        {
            var a = Path.Combine(generation.Workspace, "frame-1.jpg"); var b = Path.Combine(generation.Workspace, "frame-2.jpg");
            File.WriteAllBytes(a, [1]); File.WriteAllBytes(b, [2]);
            old = generation.Publish([new(0, a), new(1, b)])[1].Path;
        }
        using (var generation = new VideoKeyframeGeneration(cache))
        {
            var frame = Path.Combine(generation.Workspace, "frame-1.jpg"); File.WriteAllBytes(frame, [3]);
            var current = Assert.Single(generation.Publish([new(0, frame)]));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(current.Path)!));
            Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(old));
        }
        string pending;
        using (var generation = new VideoKeyframeGeneration(cache)) { pending = generation.Workspace; File.WriteAllBytes(Path.Combine(pending, "partial.jpg"), [4]); }
        Assert.False(Directory.Exists(pending)); Assert.True(File.Exists(old));
    }

    [Fact]
    public void SourceCachePurgePreservesOtherContentGenerationAndClearsUnreferencedGeneration()
    {
        var source = Path.Combine(_root, "clip.mp4"); var cache = VideoRecordPurger.SourceCacheDirectory(source);
        var shared = Path.Combine(cache, "generation-shared", "frame.jpg"); var removed = Path.Combine(cache, "generation-removed", "frame.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!); Directory.CreateDirectory(Path.GetDirectoryName(removed)!);
        File.WriteAllBytes(shared, [1]); File.WriteAllBytes(removed, [2]);
        VideoRecordPurger.PurgeSourceCaches(new(source, [removed], [shared]));
        Assert.True(File.Exists(shared)); Assert.False(File.Exists(removed));
        VideoRecordPurger.PurgeSourceCaches(new(source, [shared])); Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public void PurgeRemovesOwnedSourceCacheButPreservesNearbyOriginalAndUnownedRecordedFile()
    {
        using var db = new PicerDatabase(Path.Combine(_root, "picer.db"));
        var source = db.AddSource(_root); var original = Path.Combine(_root, "clip.mp4");
        File.WriteAllBytes(original, [1, 2, 3]);
        var videos = new VideoRepository(db); var video = videos.Upsert(source.Id, original);
        var owned = VideoRecordPurger.SourceCacheDirectory(original); Directory.CreateDirectory(owned);
        File.WriteAllBytes(Path.Combine(owned, "frame.jpg"), [4]);
        var nearby = Path.Combine(_root, "family-photo.jpg"); File.WriteAllBytes(nearby, [5]);
        videos.SaveKeyframes(video.Id, [new VideoKeyframe(0, nearby)]);
        Assert.True(new VideoRecordPurger(db).Purge(video.Id));
        Assert.False(Directory.Exists(owned)); Assert.True(File.Exists(original)); Assert.True(File.Exists(nearby));
    }

    [Fact]
    public void PurgeKeepsSharedEvidenceUntilLastReferenceAndNeverDeletesOriginals()
    {
        using var db = new PicerDatabase(Path.Combine(_root, "picer.db"));
        var source = db.AddSource(_root);
        var firstPath = Path.Combine(_root, "a.mp4"); var secondPath = Path.Combine(_root, "b.mp4");
        File.WriteAllBytes(firstPath, [1, 2, 3]); File.WriteAllBytes(secondPath, [1, 2, 3]);
        var videos = new VideoRepository(db);
        var first = videos.Upsert(source.Id, firstPath); var second = videos.Upsert(source.Id, secondPath);
        videos.SaveTranscript(first.Id, "test", "preserved transcript");
        var cache = Path.Combine(_root, "video-keyframes", first.Id.ToString()); Directory.CreateDirectory(cache);
        var frame = Path.Combine(cache, "frame.jpg"); File.WriteAllBytes(frame, [4]); File.WriteAllBytes(Path.Combine(cache, "poster.jpg"), [5]);
        using (var insert = db.Command("INSERT INTO video_keyframes(video_id,timestamp_seconds,path,created_utc) VALUES($id,0,$path,$utc)"))
        {
            insert.Parameters.AddWithValue("$id", videos.GetContentId(first.Id)); insert.Parameters.AddWithValue("$path", frame);
            insert.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O")); insert.ExecuteNonQuery();
        }
        var purger = new VideoRecordPurger(db);
        Assert.True(purger.Purge(first.Id));
        Assert.Equal("preserved transcript", videos.GetTranscript(second.Id)); Assert.True(File.Exists(frame));
        Assert.True(purger.Purge(second.Id));
        Assert.Empty(videos.GetAll()); Assert.False(Directory.Exists(cache));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(firstPath)); Assert.True(File.Exists(secondPath));
        foreach (var table in new[] { "video_contents", "video_transcripts", "video_keyframes" })
        { using var count = db.Command("SELECT COUNT(*) FROM " + table); Assert.Equal(0L, (long)count.ExecuteScalar()!); }
    }

    [Fact]
    public async Task PurgeCancelsAndWaitsForCacheWriterAndRejectsNewWriterUntilFinished()
    {
        var path = Path.Combine(_root, "clip.mp4");
        using var writer = VideoOperationLifetime.Begin(path, CancellationToken.None);
        var pending = VideoOperationLifetime.BeginPurgeAsync(path, CancellationToken.None);
        Assert.True(writer.Token.IsCancellationRequested); Assert.False(pending.IsCompleted);
        Assert.Throws<OperationCanceledException>(() => VideoOperationLifetime.Begin(path, CancellationToken.None));
        writer.Dispose();
        using (await pending) Assert.Throws<OperationCanceledException>(() => VideoOperationLifetime.Begin(path, CancellationToken.None));
        using var next = VideoOperationLifetime.Begin(path, CancellationToken.None);
        Assert.False(next.Token.IsCancellationRequested);
    }

    [Fact]
    public void JournalPurgeRemovesLinkedAgentRequestsButKeepsUnrelatedTasks()
    {
        var journal = new TaskJournal(_root);
        var request = Guid.NewGuid().ToString("N"); var child = Guid.NewGuid().ToString("N"); var other = Guid.NewGuid().ToString("N");
        TaskJournal.VideoContext.Value = 17;
        try
        {
            journal.Append(request, "/api/service/v1/video-keyframes/17", "succeeded");
            journal.Append(child, "ffmpeg.keyframes", "succeeded");
        }
        finally { TaskJournal.VideoContext.Value = null; }
        journal.Append(other, "unrelated", "succeeded");
        Assert.Equal(2, journal.GetVideoRequestIds(17).Count);
        journal.ForgetRequests(journal.GetVideoRequestIds(17));
        Assert.Empty(journal.GetVideoRequestIds(17));
        Assert.Equal(other, Assert.Single(journal.Read()).GetProperty("requestId").GetString());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
