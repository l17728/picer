using System.Security.Cryptography;
using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class MediaConsistencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-maintenance-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly long _source;
    public MediaConsistencyTests()
    {
        Directory.CreateDirectory(_root);
        _db = new(Path.Combine(_root, "picer.db"));
        _source = _db.AddSource(_root).Id;
    }
    private MediaConsistencyCandidate Add(string name, bool video = false)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var info = new FileInfo(path);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        DirectoryMediaImport.Apply(_db, _source, _root, new("seed", 1, 1, true,
            [new(path, info.Length, info.LastWriteTimeUtc, video, hash, new(10, 20, null))]));
        var id = video ? new VideoRepository(_db).GetAll().Single(x => x.Path == path).Id : _db.GetImages(_source).Single(x => x.Path == path).Id;
        return new(id, _source, _root, path, info.Length, info.LastWriteTimeUtc, hash, video);
    }
    [Fact]
    public void ForegroundPreemptsBackgroundWithoutWaiting()
    {
        var coordinator = new BackgroundWorkCoordinator(TimeSpan.Zero);
        using var background = coordinator.TryEnterBackground(default)!;
        Assert.NotNull(background);
        Assert.Null(coordinator.TryEnterBackground(default));
        using (coordinator.EnterForeground())
        {
            Assert.True(background.Token.IsCancellationRequested);
            background.Dispose();
            Assert.Null(coordinator.TryEnterBackground(default));
        }
        using var next = coordinator.TryEnterBackground(default);
        Assert.NotNull(next);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditFindsSameSignatureChangesWhileFastCheckAvoidsDecode(bool video)
    {
        var candidate = Add(video ? "test.mp4" : "test.jpg", video);
        File.WriteAllBytes(candidate.Path, [4, 3, 2, 1]);
        File.SetLastWriteTimeUtc(candidate.Path, candidate.ModifiedUtc);
        var decodes = 0;
        var verifier = new MediaConsistencyVerifier((_, _, _) => { decodes++; return Task.FromResult(new DirectoryMediaMetadata(30, 40, null)); });
        var fast = await verifier.VerifyAsync(new([candidate]), [_root], default);
        Assert.Equal("unchanged", Assert.Single(fast.Items).State);
        Assert.Equal(0, decodes);
        var audit = await verifier.VerifyAsync(new([candidate], 0), [_root], default);
        var result = Assert.Single(audit.Items);
        Assert.Equal("verified", result.State);
        Assert.Equal(1, decodes);
        Assert.NotEqual(candidate.Sha256, result.Entry!.Sha256);
        Assert.True(DirectoryMediaImport.ApplyVerifiedExisting(_db, candidate, result));
        if (video) Assert.Equal(30, new VideoRepository(_db).Get(candidate.Id)!.Width);
        else Assert.Equal(30, _db.GetImage(candidate.Id)!.Width);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, File.ReadAllBytes(candidate.Path));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingIsRetainedAndPurgedReferenceCannotBeResurrected(bool video)
    {
        var candidate = Add(video ? "gone.mp4" : "gone.jpg", video);
        File.Delete(candidate.Path);
        var result = Assert.Single((await new MediaConsistencyVerifier().VerifyAsync(new([candidate]), [_root], default)).Items);
        Assert.Equal("missing", result.State);
        Assert.True(DirectoryMediaImport.ApplyVerifiedExisting(_db, candidate, result));
        if (video) Assert.Equal("missing", new VideoRepository(_db).Get(candidate.Id)!.Status);
        else Assert.Equal(ImageStatus.Missing, _db.GetImage(candidate.Id)!.Status);
        using (var command = _db.Command($"DELETE FROM {(video ? "videos" : "images")} WHERE id=$id"))
        { command.Parameters.AddWithValue("$id", candidate.Id); command.ExecuteNonQuery(); }
        var verified = new MediaConsistencyResult(candidate.Id, video, "verified", new(candidate.Path, candidate.FileSize,
            candidate.ModifiedUtc, video, candidate.Sha256, null, Reused: true));
        Assert.False(DirectoryMediaImport.ApplyVerifiedExisting(_db, candidate, verified));
        if (video) Assert.Null(new VideoRepository(_db).Get(candidate.Id)); else Assert.Null(_db.GetImage(candidate.Id));
    }
    [Fact]
    public async Task OfflineOrUnauthorizedRootIsUnknown()
    {
        var candidate = Add("offline.jpg");
        File.Delete(candidate.Path);
        var offline = candidate with { Root = Path.Combine(_root, "offline"), Path = Path.Combine(_root, "offline", "photo.jpg") };
        var verifier = new MediaConsistencyVerifier();
        Assert.Equal("unknown", Assert.Single((await verifier.VerifyAsync(new([offline]), [_root], default)).Items).State);
        Assert.Equal("unknown", Assert.Single((await verifier.VerifyAsync(new([candidate]), [Path.Combine(_root, "other")], default)).Items).State);
    }
    [Fact]
    public async Task CancellationReturnsPartialBatchAndDoesNotPublishIncompleteHash()
    {
        var first = Add("one.jpg"); var second = Add("two.jpg");
        using var stop = new CancellationTokenSource();
        var verifier = new MediaConsistencyVerifier((_, _, token) => { stop.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(new DirectoryMediaMetadata(1, 1, null)); });
        second = second with { Sha256 = new string('a', 64) };
        var batch = await verifier.VerifyAsync(new([first, second], 1), [_root], stop.Token);
        Assert.True(batch.Deferred);
        Assert.Equal(first.Id, Assert.Single(batch.Items).Id);
    }
    [Fact]
    public async Task CheckpointsResumeAndAlternateMediaWithoutWholeTreeScan()
    {
        var a = Add("a.jpg"); var b = Add("b.jpg"); var v = Add("v.mp4", true);
        var checkpoint = Path.Combine(_root, "checkpoint.json");
        var requests = new List<MediaConsistencyRequest>();
        Task<MediaConsistencyBatch?> Verify(MediaConsistencyRequest request, CancellationToken token)
        { requests.Add(request); return Task.FromResult<MediaConsistencyBatch?>(new(request.Items.Select(x => new MediaConsistencyResult(x.Id, x.IsVideo, "unchanged")).ToArray())); }
        var maintenance = new MediaConsistencyMaintenance(_db.OpenIndependentConnection, Verify, checkpoint, 1);
        await maintenance.RunOnceAsync(default);
        maintenance = new(_db.OpenIndependentConnection, Verify, checkpoint, 1);
        await maintenance.RunOnceAsync(default);
        await maintenance.RunOnceAsync(default);
        Assert.Equal(a.Id, Assert.Single(requests[0].Items).Id);
        Assert.True(Assert.Single(requests[1].Items).IsVideo);
        Assert.Equal(v.Id, requests[1].Items[0].Id);
        Assert.Equal(b.Id, Assert.Single(requests[2].Items).Id);
        Assert.DoesNotContain(_root, File.ReadAllText(checkpoint));
    }
    [Fact]
    public void StaleResultsCannotOverwriteForegroundUpdates()
    {
        var candidate = Add("moved.jpg");
        using (var command = _db.Command("UPDATE images SET path=$path WHERE id=$id"))
        { command.Parameters.AddWithValue("$path", Path.Combine(_root, "new.jpg")); command.Parameters.AddWithValue("$id", candidate.Id); command.ExecuteNonQuery(); }
        Assert.False(DirectoryMediaImport.ApplyVerifiedExisting(_db, candidate, new(candidate.Id, false, "missing")));
        Assert.Equal(ImageStatus.Active, _db.GetImage(candidate.Id)!.Status);
    }
    public void Dispose() { _db.Dispose(); Directory.Delete(_root, true); }
}
