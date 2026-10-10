using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Picer.Core.Gateway;
using Picer.Core.Imaging;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class MediaStreamingTests
{
    [Fact]
    public async Task IfRangeMismatch_ReturnsCompleteRepresentationDespiteReadAheadLimit()
    {
        using var fixture = new GatewayServiceFixture();
        var path = Path.Combine(fixture.Root, "if-range.mp4");
        var content = Enumerable.Range(0, 17).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(path, content);
        var video = new VideoRepository(fixture.Db).Upsert(fixture.SourceId, path);
        await using var agent = await TestAgent.StartAsync(fixture);
        using var client = CreateClient(fixture);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/gateway/v1/assets/videos/{video.Id}");
        request.Headers.Add("Range", "bytes=0-3");
        request.Headers.Add("If-Range", "\"older-version\"");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(content.Length, agent.BytesRead);
    }

    [Fact]
    public async Task LegacyAgent_HeadDoesNotRequestOriginal_AndReportsDownloadFallback()
    {
        using var fixture = new GatewayServiceFixture();
        var path = Path.Combine(fixture.Root, "legacy-large.mp4");
        await using (var file = File.Create(path)) file.SetLength(80L * 1024 * 1024);
        var video = new VideoRepository(fixture.Db).Upsert(fixture.SourceId, path);
        await using var agent = await TestAgent.StartAsync(fixture, binary: false);
        using var gateway = new HttpIndexGateway(fixture.BaseUrl, "picer-test-token", fixture.SpkiPin);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var info = await gateway.GetMediaInfoAsync(video.Id, true, deadline.Token);
        Assert.False(info.SupportsStreaming);
        Assert.Equal(0, agent.OriginalRequests);
        Assert.Equal(0, agent.BytesRead);
    }

    [Fact]
    public async Task RealAgentChannel_LargeFileSeeksWithoutFullTransfer_AndClosesSource()
    {
        using var fixture = new GatewayServiceFixture();
        var path = Path.Combine(fixture.Root, "large.mp4");
        await using (var file = File.Create(path))
        {
            file.SetLength(80L * 1024 * 1024);
            await file.WriteAsync(new byte[] { 1, 2, 3, 4 });
            file.Position = file.Length - 4;
            await file.WriteAsync(new byte[] { 5, 6, 7, 8 });
        }
        var video = new VideoRepository(fixture.Db).Upsert(fixture.SourceId, path);
        await using var agent = await TestAgent.StartAsync(fixture);
        using var client = CreateClient(fixture);
        string etag;
        using (var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"api/gateway/v1/assets/videos/{video.Id}")))
        {
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            etag = head.Headers.ETag!.ToString();
            Assert.StartsWith("\"lease-", etag);
            Assert.Equal(80L * 1024 * 1024, head.Content.Headers.ContentLength);
            Assert.Equal("1", head.Headers.GetValues("X-Picer-Media-Streaming").Single());
            Assert.Equal(0, agent.BytesRead);
        }
        foreach (var (range, expected) in new[] { ("bytes=0-3", new byte[] { 1, 2, 3, 4 }), ("bytes=-4", new byte[] { 5, 6, 7, 8 }) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/gateway/v1/assets/videos/{video.Id}");
            request.Headers.Add("Range", range);
            request.Headers.Add("If-Match", etag);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(8, agent.BytesRead);
        await agent.WaitForIdleAsync();
        Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None));
        agent.Release(etag);
        using var writable = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None);
        Assert.Equal(80L * 1024 * 1024, writable.Length);
    }

    [Fact]
    public async Task GatewayRanges_RejectExpiredLease_AndReleaseSourceOnDispose()
    {
        using var fixture = new GatewayServiceFixture();
        var path = Path.Combine(fixture.Root, "range.mp4");
        await File.WriteAllBytesAsync(path, new byte[2 * MediaWire.BlockSize]);
        var video = new VideoRepository(fixture.Db).Upsert(fixture.SourceId, path);
        await using var agent = await TestAgent.StartAsync(fixture);
        using var gateway = new HttpIndexGateway(fixture.BaseUrl, "picer-test-token", fixture.SpkiPin);
        var info = await gateway.GetMediaInfoAsync(video.Id, true);
        Assert.True(info.SupportsStreaming);
        await using (var reader = new ParallelMediaReader(gateway, video.Id, true, info, 4))
        {
            Assert.Equal(MediaWire.BlockSize, (await reader.ReadBlockAsync(1, CancellationToken.None)).Length);
            agent.Release(info.ETag);
            await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadBlockAsync(0, CancellationToken.None));
        }
        await agent.WaitForIdleAsync();
        using var writable = File.OpenWrite(path);
    }

    [Fact]
    public async Task PlayableCache_FailedPurgeDoesNotPoisonNextDownload()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new PlayableMediaCache(Path.Combine(root, "remote-assets", "playable"), 1024);
            var file = await cache.GetAsync(new string('a', 64), ".mp4", null, (p, ct) => File.WriteAllBytesAsync(p, new byte[8], ct), CancellationToken.None);
            using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read)) Assert.Throws<IOException>(() => PlayableMediaCache.Purge(root));
            var next = await cache.GetAsync(new string('b', 64), ".mp4", null, (p, ct) => File.WriteAllBytesAsync(p, new byte[8], ct), CancellationToken.None);
            Assert.True(File.Exists(next));
            PlayableMediaCache.Purge(root);
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(next)!));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ReadLease_RejectsReleasedVersionAndOtherPath_AndCannotReopenAfterDispose()
    {
        var path = Path.GetTempFileName();
        try
        {
            var pool = new MediaReadLeasePool();
            string tag;
            using (var lease = pool.Acquire(path))
            {
                tag = lease.ETag;
                Assert.Throws<MediaSourceChangedException>(() => pool.Acquire(path + ".other", tag));
                pool.Release(tag);
                Assert.Throws<MediaSourceChangedException>(() => pool.Acquire(path, tag));
                Assert.Throws<IOException>(() => File.OpenWrite(path));
            }
            using (File.OpenWrite(path)) { }
            pool.Dispose();
            Assert.Throws<ObjectDisposedException>(() => pool.Acquire(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SourceThumbnail_TransfersOnlyResizedPixels_AndCoalescesRequests()
    {
        using var fixture = new GatewayServiceFixture();
        var path = Path.Combine(fixture.Root, "large-image.bmp");
        using (var image = new RasterImage(3000, 2000, new RgbPixel(20, 30, 50))) image.SaveAsPng(path);
        var record = new Picer.Core.Models.ImageRecord { SourceId = fixture.SourceId, Path = @"Z:\remote-test\large-image.bmp", FileSize = new FileInfo(path).Length, ModifiedUtc = DateTime.UtcNow, Width = 3000, Height = 2000 };
        fixture.Db.UpsertImage(record);
        var id = fixture.Db.GetImages(fixture.SourceId).Single(item => item.Path == record.Path).Id;
        await using var agent = await TestAgent.StartAsync(fixture);
        using var client = CreateClient(fixture);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetByteArrayAsync($"api/gateway/v1/thumbs/{id}")));
        foreach (var bytes in results)
        {
            using var input = new MemoryStream(bytes);
            using var image = RasterImage.Load(input);
            Assert.InRange(image.Width, 1, 256); Assert.InRange(image.Height, 1, 256);
        }
        Assert.Equal(1, agent.MediaConnections);
        Assert.InRange(agent.BytesRead, 1, 64 * 1024);
    }

    [Fact]
    public async Task ParallelDownload_RespectsBound_ReusesBlocks_AndRejectsChangedVersion()
    {
        var gateway = new FakeRangeGateway();
        var info = await gateway.GetMediaInfoAsync(1, true);
        var reader = new ParallelMediaReader(gateway, 1, true, info, 4);
        using var output = new MemoryStream();
        await reader.CopyRangeAsync(output, MediaWire.BlockSize - 2, 8, CancellationToken.None);
        var requests = gateway.Requests;
        using var repeat = new MemoryStream();
        await reader.CopyRangeAsync(repeat, MediaWire.BlockSize - 2, 8, CancellationToken.None);
        Assert.Equal(requests, gateway.Requests);
        Assert.Equal(output.ToArray(), repeat.ToArray());
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin");
        try
        {
            await reader.DownloadAsync(path, CancellationToken.None);
            Assert.Equal(info.Length, new FileInfo(path).Length);
            Assert.InRange(gateway.Peak, 2, 4);
            gateway.Changed = true;
            var fresh = new ParallelMediaReader(gateway, 1, true, info);
            await Assert.ThrowsAsync<InvalidDataException>(() => fresh.ReadBlockAsync(0, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PreviewCache_EvictsOldBlocksWithinCapacity()
    {
        var cache = new MediaPreviewCache(2 * MediaWire.BlockSize);
        var calls = 0;
        Task<byte[]> Make(CancellationToken _) { calls++; return Task.FromResult(new byte[MediaWire.BlockSize]); }
        foreach (var key in new[] { "one", "two", "three", "one" }) await cache.GetAsync(key, Make, CancellationToken.None);
        Assert.Equal(4, calls);
        await cache.GetAsync("three", Make, CancellationToken.None);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task SaturatedOriginalReaders_LeavePreviewChannelAvailable()
    {
        using var fixture = new GatewayServiceFixture();
        var path = Path.Combine(fixture.Root, "busy.mp4");
        using (var file = File.Create(path)) file.SetLength(80L * 1024 * 1024);
        var video = new VideoRepository(fixture.Db).Upsert(fixture.SourceId, path);
        var record = new Picer.Core.Models.ImageRecord { SourceId = fixture.SourceId, Path = @"Z:\remote-test\img0.png", FileSize = 10, ModifiedUtc = DateTime.UtcNow };
        fixture.Db.UpsertImage(record);
        var id = fixture.Db.GetImages(fixture.SourceId).Single(item => item.Path == record.Path).Id;
        await using var agent = await TestAgent.StartAsync(fixture);
        using var client = CreateClient(fixture);
        var responses = new List<HttpResponseMessage>();
        try
        {
            for (var i = 0; i < 4; i++) responses.Add(await client.GetAsync($"api/gateway/v1/assets/videos/{video.Id}", HttpCompletionOption.ResponseHeadersRead));
            var bytes = await client.GetByteArrayAsync($"api/gateway/v1/thumbs/{id}").WaitAsync(TimeSpan.FromSeconds(20));
            using var input = new MemoryStream(bytes);
            using var image = RasterImage.Load(input);
            Assert.Equal(2, image.Width);
        }
        finally { foreach (var response in responses) response.Dispose(); }
        await agent.WaitForIdleAsync();
    }

    [Fact]
    public async Task PreviewClear_PreventsInflightPublication()
    {
        var cache = new MediaPreviewCache(1024);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = cache.GetAsync("image", async _ => { entered.SetResult(); await release.Task; return new byte[5]; }, CancellationToken.None);
        await entered.Task; cache.Clear(); release.SetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData(VideoPlaybackMode.Automatic, VideoPlayerKind.Unknown, true, true, false)]
    [InlineData(VideoPlaybackMode.Automatic, VideoPlayerKind.Vlc, true, true, true)]
    [InlineData(VideoPlaybackMode.PreferStreaming, VideoPlayerKind.Unknown, true, true, true)]
    [InlineData(VideoPlaybackMode.PreferStreaming, VideoPlayerKind.Mpv, false, true, false)]
    [InlineData(VideoPlaybackMode.Download, VideoPlayerKind.Vlc, true, true, false)]
    public void PlayerPolicy_FallsBackConservatively(VideoPlaybackMode mode, VideoPlayerKind player, bool service, bool executable, bool expected) =>
        Assert.Equal(expected, VideoPlayerPolicy.ShouldStream(mode, player, service, executable));

    private static HttpClient CreateClient(GatewayServiceFixture fixture)
    {
        var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null && HttpIndexGateway.ComputeSpkiPin(certificate).SequenceEqual(fixture.SpkiPin) }) { BaseAddress = new Uri(fixture.BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", "picer-test-token");
        return client;
    }

    private sealed class FakeRangeGateway : IRangeMediaGateway
    {
        private int _active;
        public int Peak, Requests;
        public bool Changed;
        public Task<RemoteMediaInfo> GetMediaInfoAsync(long id, bool video, CancellationToken token = default) => Task.FromResult(new RemoteMediaInfo(8L * MediaWire.BlockSize, "\"version1\"", "video/mp4", true));
        public async Task<Stream> OpenMediaRangeAsync(long id, bool video, RemoteMediaInfo expected, long offset, int count, CancellationToken token = default)
        {
            if (Changed) throw new InvalidDataException("changed");
            Interlocked.Increment(ref Requests);
            var active = Interlocked.Increment(ref _active);
            int peak; do { peak = Peak; } while (active > peak && Interlocked.CompareExchange(ref Peak, active, peak) != peak);
            try
            {
                await Task.Delay(25, token);
                var bytes = new byte[count]; Array.Fill(bytes, (byte)(offset / MediaWire.BlockSize));
                return new MemoryStream(bytes);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class TestAgent(GatewayServiceFixture fixture) : IAsyncDisposable
    {
        private readonly ClientWebSocket _control = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _requests = [];
        private readonly MediaReadLeasePool _leases = new();
        public void Release(string tag) => _leases.Release(tag);
        private Task? _loop;
        private long _bytes;
        private int _connections, _active;
        public long BytesRead => Interlocked.Read(ref _bytes);
        public int MediaConnections => _connections;
        public int OriginalRequests;
        public static async Task<TestAgent> StartAsync(GatewayServiceFixture fixture, bool binary = true)
        {
            var agent = new TestAgent(fixture);
            agent.Configure(agent._control);
            await agent._control.ConnectAsync(agent.Endpoint("api/agent/v1/connect"), agent._stop.Token);
            await MediaWire.SendJsonAsync(agent._control, new { type = "agent.register", agentId = "binary-test", capabilities = binary ? new[] { MediaWire.Capability } : Array.Empty<string>() }, agent._stop.Token);
            await MediaWire.ReceiveJsonAsync<JsonElement>(agent._control, agent._stop.Token);
            agent._loop = agent.LoopAsync(); return agent;
        }
        private Uri Endpoint(string path) => new(new UriBuilder(fixture.BaseUrl) { Scheme = "wss" }.Uri, path);
        private void Configure(ClientWebSocket socket)
        {
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) => MediaAgentSettings.MatchesCertificate(certificate, fixture.SpkiPin);
            socket.Options.SetRequestHeader("Authorization", "Bearer picer-test-token");
        }
        private async Task LoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var request = await MediaWire.ReceiveJsonAsync<JsonElement>(_control, _stop.Token);
                    switch (request.GetProperty("type").GetString())
                    {
                        case "asset.open": Interlocked.Increment(ref OriginalRequests); break;
                        case "media.open": _requests.Add(ServeAsync(request)); break;
                        case "media.release": _leases.Release(request.GetProperty("etag").GetString()!); break;
                        case "media.keepalive": _leases.Touch(request.GetProperty("etag").GetString()!); break;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or WebSocketException or OperationCanceledException) { }
        }
        private async Task ServeAsync(JsonElement request)
        {
            Interlocked.Increment(ref _connections); Interlocked.Increment(ref _active);
            using var socket = new ClientWebSocket(); Configure(socket);
            try
            {
                await socket.ConnectAsync(Endpoint("api/agent/v1/media/" + request.GetProperty("requestId").GetString()), _stop.Token);
                long last = 0;
                var requestedPath = request.GetProperty("path").GetString()!;
                if (!File.Exists(requestedPath)) requestedPath = Path.Combine(fixture.Root, Path.GetFileName(requestedPath));
                await MediaFileSession.ServeAsync(socket, requestedPath, [fixture.Root], request.GetProperty("previewSize").GetInt32(), "video/mp4", new MediaPreviewCache(), metrics =>
                {
                    var total = long.Parse(metrics.Split(';')[0].Split('=')[1]);
                    Interlocked.Add(ref _bytes, total - last); last = total;
                }, _stop.Token, _leases, request.GetProperty("expectedETag").GetString(), request.GetProperty("keepLease").GetBoolean());
            }
            catch (MediaSourceChangedException)
            {
                await MediaWire.SendJsonAsync(socket, new MediaWire.Metadata(0, "", "", "media_source_changed"), _stop.Token);
            }
            catch (Exception exception) when (exception is IOException or WebSocketException or OperationCanceledException) { }
            finally { Interlocked.Decrement(ref _active); }
        }
        public async Task WaitForIdleAsync()
        {
            var deadline = Stopwatch.StartNew();
            while (Volatile.Read(ref _active) > 0 && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            Assert.Equal(0, _active);
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _control.Abort();
            if (_loop is not null) await _loop;
            await Task.WhenAll(_requests);
            _leases.Dispose();
            _control.Dispose(); _stop.Dispose();
        }
    }
}
