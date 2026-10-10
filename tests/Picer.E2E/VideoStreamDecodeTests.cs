using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using Picer.App.Services;
using Picer.Core.Gateway;

namespace Picer.E2E;

public sealed class VideoStreamDecodeTests(Xunit.Abstractions.ITestOutputHelper evidence)
{
    [Fact]
    [Trait("Category", "RuntimeComponent")]
    public async Task RealDecoder_ReadsTailMoovAndSeeks_WithoutDownloadingWholeVideo()
    {
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "runtime", "ffmpeg", "ffmpeg.exe");
        Assert.True(File.Exists(ffmpeg), "Pinned offline FFmpeg is required.");
        var root = Path.Combine(Path.GetTempPath(), "picer-stream-decode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = Path.Combine(root, "small.mp4");
            await RunAsync(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10", "-t", "4", "-c:v", "mpeg4", original]);
            var small = await File.ReadAllBytesAsync(original);
            var moov = 0;
            while (moov < small.Length && Encoding.ASCII.GetString(small, moov + 4, 4) != "moov")
                moov += checked((int)BinaryPrimitives.ReadUInt32BigEndian(small.AsSpan(moov, 4)));
            Assert.InRange(moov, 8, small.Length - 8);
            var large = Path.Combine(root, "tail-moov.mp4");
            const int padding = 80 * 1024 * 1024;
            await using (var output = File.Create(large))
            {
                await output.WriteAsync(small.AsMemory(0, moov));
                var header = new byte[8]; BinaryPrimitives.WriteUInt32BigEndian(header, padding); Encoding.ASCII.GetBytes("free").CopyTo(header, 4);
                await output.WriteAsync(header);
                output.Position = moov + padding;
                await output.WriteAsync(small.AsMemory(moov));
            }
            var source = new FileSource(large);
            await using var reader = new ParallelMediaReader(source, 1, true, source.Info);
            await using var proxy = await VideoPlaybackProxy.StartAsync(reader, CancellationToken.None);
            foreach (var position in new[] { "0", "3", "1" })
            {
                var elapsed = Stopwatch.StartNew();
                var frame = Path.Combine(root, "frame-" + position + ".png");
                await RunAsync(ffmpeg, ["-v", "error", "-ss", position, "-i", proxy.Url, "-frames:v", "1", frame]);
                Assert.True(new FileInfo(frame).Length > 100);
                evidence.WriteLine($"seekSeconds={position}; decodedFrameMs={elapsed.ElapsedMilliseconds}; sourceBytes={source.Bytes}; fileBytes={source.Info.Length}; processPeakWorkingSet={Process.GetCurrentProcess().PeakWorkingSet64}");
            }
            Assert.True(source.Bytes < source.Info.Length / 2, $"Read {source.Bytes} / {source.Info.Length} bytes");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task RunAsync(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        // Decoder errors may contain the ephemeral local playback capability: never echo stderr.
        await error;
        Assert.Equal(0, process.ExitCode);
    }
    private sealed class FileSource(string path) : IRangeMediaGateway
    {
        public RemoteMediaInfo Info { get; } = new(new FileInfo(path).Length, "\"tail-moov-fixture\"", "video/mp4", true);
        public long Bytes;
        public Task<RemoteMediaInfo> GetMediaInfoAsync(long id, bool video, CancellationToken token = default) => Task.FromResult(Info);
        public async Task<Stream> OpenMediaRangeAsync(long id, bool video, RemoteMediaInfo expected, long offset, int count, CancellationToken token = default)
        {
            await using var file = File.OpenRead(path);
            file.Position = offset;
            var bytes = new byte[count]; await file.ReadExactlyAsync(bytes.AsMemory(), token);
            Interlocked.Add(ref Bytes, count);
            return new MemoryStream(bytes, writable: false);
        }
    }
}
