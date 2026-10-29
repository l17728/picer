using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using Picer.App.Infrastructure;
using Picer.App.ViewModels;
using Picer.Core.Data;
using Picer.Core.Video;

namespace Picer.E2E;

public sealed class InteractiveReadConcurrencyTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void CachedFrameAppearsWhileAnotherFrameDownloadIsPending()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-cached-read-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "picer.db");
        using (var setup = new PicerDatabase(path))
        {
            var source = setup.AddSource(root);
            var media = Path.Combine(root, "video.mp4");
            File.WriteAllBytes(media, [1]);
            var repository = new VideoRepository(setup);
            var video = repository.Upsert(source.Id, media);
            repository.SaveKeyframes(video.Id, [new VideoKeyframe(0, "remote-0.jpg"), new VideoKeyframe(1, "remote-1.jpg")]);
            var frame = repository.GetKeyframes(video.Id, existingOnly: false)[0];
            var directory = Path.Combine(root, "remote-video-keyframes", video.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, $"v2-{frame.Id}-0-{frame.DifferenceHash}.jpg"), [1]);
        }
        var handler = new PendingFrameHandler();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    using var database = new PicerDatabase(path);
                    var repository = new VideoRepository(database);
                    using var client = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") };
                    using var gateway = new Picer.Core.Gateway.HttpIndexGateway(client);
                    var services = new Picer.App.ClientServices { Db = database, Videos = repository, Gateway = gateway, RemoteVideoCacheRoot = root };
                    var model = new VideoViewModel(repository, new VideoIndexService(database, repository), null, remoteMode: true, services: services);
                    await model.LoadAsync();
                    model.SelectedVideo = Assert.Single(model.Videos);
                    try
                    {
                        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert.Single(model.Frames);
                        Assert.False(handler.Release.Task.IsCompleted);
                    }
                    finally { handler.Release.TrySetResult(); }
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (!model.StatusText.Contains("部分暂不可用") && DateTime.UtcNow < deadline) await Task.Delay(20);
                    Assert.Contains("部分暂不可用", model.StatusText);
                    Assert.Single(model.Frames); // No duplicate when final results replace the early cache.
                }
                catch (Exception exception) { failure = exception; }
                finally { handler.Release.TrySetResult(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var completed = thread.Join(TimeSpan.FromSeconds(25));
        handler.Release.TrySetResult();
        Assert.True(completed, "缓存关键帧并发用例超时");
        try { Assert.Null(failure); }
        finally { Directory.Delete(root, true); }
    }

    private sealed class PendingFrameHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public void VideoCatalogAndDetailsLoadWhileBackgroundWriterOwnsDatabaseGate()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-interactive-read-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "picer.db");
        using (var setup = new PicerDatabase(path))
        {
            var source = setup.AddSource(root);
            var media = Path.Combine(root, "video.mp4");
            var frame = Path.Combine(root, "frame.jpg");
            File.WriteAllBytes(media, [1]);
            File.WriteAllBytes(frame, [1]);
            var repository = new VideoRepository(setup);
            var video = repository.Upsert(source.Id, media);
            repository.SaveKeyframes(video.Id, [new VideoKeyframe(0, frame)]);
            repository.SaveTranscript(video.Id, "test", "{\"text\":\"committed\"}");
        }
        Exception? failure = null;
        using var releaseWriter = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    using var database = new PicerDatabase(path);
                    var repository = new VideoRepository(database);
                    var videoId = Assert.Single(repository.GetAll()).Id;
                    var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var writing = UiWork.RunDatabaseAsync("Test.HoldSyncWriter", () =>
                    {
                        using var transaction = database.BeginTransaction();
                        using var update = database.Command("UPDATE video_transcripts SET transcript_json=$json WHERE video_id=(SELECT content_id FROM videos WHERE id=$id)");
                        update.Parameters.AddWithValue("$json", "{\"text\":\"uncommitted\"}");
                        update.Parameters.AddWithValue("$id", videoId);
                        update.ExecuteNonQuery();
                        writerStarted.SetResult();
                        if (!releaseWriter.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("writer release");
                        transaction.Commit();
                    });
                    try
                    {
                        await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        var model = new VideoViewModel(repository, new VideoIndexService(database, repository), null);
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        await model.LoadAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        model.SelectedVideo = Assert.Single(model.Videos);
                        var deadline = DateTime.UtcNow.AddSeconds(5);
                        while (model.Frames.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
                        Assert.Single(model.Frames);
                        Assert.Equal("committed", model.TranscriptText);
                        Assert.False(writing.IsCompleted);
                        output.WriteLine($"后台写锁未释放时，视频列表及详情完成耗时：{timer.ElapsedMilliseconds} ms");
                    }
                    finally { releaseWriter.Set(); await writing; }
                    Assert.Contains("uncommitted", repository.GetTranscript(videoId));
                }
                catch (Exception exception) { failure = exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var completed = thread.Join(TimeSpan.FromSeconds(35));
        releaseWriter.Set();
        Assert.True(completed, "交互读取并发用例超时");
        try { Assert.Null(failure); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MemoryFixturesUseSerializedFallback()
    {
        using var database = new PicerDatabase(":memory:");
        var sources = await UiWork.ReadSnapshotAsync("Test.MemoryRead", database, reader => reader.GetSources());
        Assert.Empty(sources);
    }
}
