using System.Diagnostics;
using System.Text.Json;
using Picer.Core.Gateway;

internal static class MediaBenchmark
{
    public static async Task RunAsync()
    {
        foreach (var concurrency in new[] { 1, 2, 4 })
        {
            var source = new Source();
            await using var reader = new ParallelMediaReader(source, 1, true, source.Info, concurrency);
            var clock = Stopwatch.StartNew();
            await reader.CopyRangeAsync(Stream.Null, 0, 64 * 1024, CancellationToken.None);
            var firstMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            await reader.CopyRangeAsync(Stream.Null, source.Info.Length - 4096, 4096, CancellationToken.None);
            var seekColdMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            await reader.CopyRangeAsync(Stream.Null, source.Info.Length - 4096, 4096, CancellationToken.None);
            var seekWarmMs = clock.Elapsed.TotalMilliseconds;
            var destination = Path.Combine(Path.GetTempPath(), "picer-media-benchmark-" + Guid.NewGuid().ToString("N"));
            try
            {
                clock.Restart();
                await reader.DownloadAsync(destination, CancellationToken.None);
                var downloadMs = clock.Elapsed.TotalMilliseconds;
                Console.WriteLine(JsonSerializer.Serialize(new { scenario = "synthetic-25ms-per-block-16MiB", concurrency, firstBytesMs = firstMs, seekColdMs, seekWarmMs, downloadMs, MiBPerSecond = 16 * 1000 / downloadMs, source.Bytes, source.Requests, source.Peak, processPeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64 }));
            }
            finally { File.Delete(destination); }
        }
    }
    private sealed class Source : IRangeMediaGateway
    {
        public RemoteMediaInfo Info { get; } = new(16L * MediaWire.BlockSize, "\"benchmark\"", "video/mp4", true);
        public long Bytes;
        public int Requests, Peak;
        private int _active;
        public Task<RemoteMediaInfo> GetMediaInfoAsync(long id, bool video, CancellationToken token = default) => Task.FromResult(Info);
        public async Task<Stream> OpenMediaRangeAsync(long id, bool video, RemoteMediaInfo expected, long offset, int count, CancellationToken token = default)
        {
            var active = Interlocked.Increment(ref _active);
            int old; do { old = Peak; } while (active > old && Interlocked.CompareExchange(ref Peak, active, old) != old);
            try
            {
                await Task.Delay(25, token);
                Interlocked.Increment(ref Requests); Interlocked.Add(ref Bytes, count);
                return new MemoryStream(new byte[count], writable: false);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
