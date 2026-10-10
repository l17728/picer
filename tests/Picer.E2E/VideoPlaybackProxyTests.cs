using System.Net;
using System.IO;
using System.Net.Http;
using Picer.App.Services;
using Picer.Core.Gateway;

namespace Picer.E2E;

public sealed class VideoPlaybackProxyTests
{
    [Fact]
    public async Task LoopbackPlayer_SeeksLargeVideo_RejectsUnauthorizedRequests()
    {
        var source = new TestSource();
        var reader = new ParallelMediaReader(source, 1, true, source.Info);
        await using var proxy = await VideoPlaybackProxy.StartAsync(reader, CancellationToken.None);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using (var request = new HttpRequestMessage(HttpMethod.Get, proxy.Url))
        {
            request.Headers.Add("Range", "bytes=-16");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(source.Info.Length - 16, response.Content.Headers.ContentRange!.From);
            Assert.Equal(Enumerable.Repeat((byte)95, 16), await response.Content.ReadAsByteArrayAsync());
        }
        await proxy.FirstRead.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, source.Requests);
        Assert.Equal(16, source.Bytes);
        using (var request = new HttpRequestMessage(HttpMethod.Get, proxy.Url))
        {
            request.Headers.Add("Range", "bytes=0-7");
            using var response = await client.SendAsync(request);
            Assert.Equal(new byte[8], await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(2, source.Requests);
        Assert.Equal(24, source.Bytes);
        foreach (var header in new[] { "Host", "Origin", "Sec-Fetch-Site" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, proxy.Url);
            request.Headers.TryAddWithoutValidation(header, header == "Host" ? "localhost" : "https://untrusted.invalid");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using (var response = await client.GetAsync(proxy.Url + "wrong")) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var request = new HttpRequestMessage(HttpMethod.Get, proxy.Url))
        {
            request.Headers.TryAddWithoutValidation("Range", "bytes=-0");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        }
        Assert.Equal(2, source.Requests);
    }

    [Fact]
    public async Task LoopbackPlayer_CancellationStopsUpstreamRead()
    {
        var source = new TestSource { Block = true };
        var reader = new ParallelMediaReader(source, 1, true, source.Info);
        await using var proxy = await VideoPlaybackProxy.StartAsync(reader, CancellationToken.None);
        using var client = new HttpClient();
        using var stop = new CancellationTokenSource();
        var read = client.GetAsync(proxy.Url, stop.Token);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await source.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class TestSource : IRangeMediaGateway
    {
        public RemoteMediaInfo Info { get; } = new(96L * MediaWire.BlockSize, "\"fixture-version\"", "video/mp4", true);
        public int Requests;
        public long Bytes;
        public bool Block;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RemoteMediaInfo> GetMediaInfoAsync(long id, bool video, CancellationToken token = default) => Task.FromResult(Info);
        public async Task<Stream> OpenMediaRangeAsync(long id, bool video, RemoteMediaInfo expected, long offset, int count, CancellationToken token = default)
        {
            Interlocked.Increment(ref Requests); Entered.TrySetResult();
            try { if (Block) await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
            var bytes = new byte[count]; Array.Fill(bytes, (byte)(offset / MediaWire.BlockSize));
            Interlocked.Add(ref Bytes, count);
            return new MemoryStream(bytes);
        }
    }
}
