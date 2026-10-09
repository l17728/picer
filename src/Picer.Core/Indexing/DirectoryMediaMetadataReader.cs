using Picer.Core.Hashing;
using Picer.Core.Metadata;
using Picer.Core.Video;

namespace Picer.Core.Indexing;

public sealed record DirectoryMediaMetadata(int Width, int Height, DateTime? TakenUtc = null,
    string? Camera = null, string? Gps = null, double? DurationSeconds = null, long AverageHash = 0, long DifferenceHash = 0);

public static class DirectoryMediaMetadataReader
{
    public static async Task<DirectoryMediaMetadata> ReadAsync(string path, bool video, CancellationToken token, string? ffmpeg = null)
    {
        token.ThrowIfCancellationRequested();
        if (!video)
        {
            var metadata = ImageMetadataReader.Read(path);
            if (metadata.Width <= 0 || metadata.Height <= 0) throw new InvalidDataException("invalid_image_metadata");
            using var stream = File.OpenRead(path);
            var average = PerceptualHasher.ComputeAverageHash(stream);
            stream.Position = 0;
            var difference = PerceptualHasher.ComputeDifferenceHash(stream);
            return new(metadata.Width, metadata.Height, metadata.TakenUtc, metadata.Camera, metadata.Gps,
                AverageHash: average, DifferenceHash: difference);
        }
        if (string.IsNullOrWhiteSpace(ffmpeg)) ffmpeg = new FfmpegLocator().Find() ?? new FfmpegLocator(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."))).Find();
        if (ffmpeg is null) throw new InvalidOperationException("ffmpeg_not_available");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var result = await new FfmpegKeyframeService(ffmpeg, Path.GetTempPath()).ProbeAsync(
                new(0, 0, path, 0, default, null, 0, 0, null, null, "active"), timeout.Token);
            return new(result.Width, result.Height, result.TakenUtc, Gps: result.Gps, DurationSeconds: result.DurationSeconds);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("video_probe_timeout"); }
    }
}
