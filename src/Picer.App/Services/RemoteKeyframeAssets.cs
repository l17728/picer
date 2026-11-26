using System.IO;
using System.Net.Http;
using Picer.App.Infrastructure;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.App.Services;

internal static class RemoteKeyframeAssets
{
    public sealed record LegacyMigrationResult(int Migrated, int Discarded, int Failed);

    public static ImageRecord Key(VideoRecord video, VideoKeyframe frame) => RemoteThumbnailCache.DerivedRecord("video-frame-v3",
        System.Text.Json.JsonSerializer.Serialize(new { Identity = video.Sha256 ?? System.Text.Json.JsonSerializer.Serialize(new { video.Id, video.FileSize, video.ModifiedUtc }), FrameId = frame.Id, frame.TimestampSeconds, frame.DifferenceHash }));

    public static async Task<string?> GetAsync(ClientServices services, VideoRecord video, VideoKeyframe frame, int ordinal,
        CancellationToken token, bool prefetch = false)
    {
        if (services.Gateway is not HttpIndexGateway gateway) return null;
        var key = Key(video, frame);
        var legacy = Path.Combine(services.RemoteVideoCacheRoot ?? AppServices.RemoteDataRoot, "remote-video-keyframes",
            video.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), $"v2-{frame.Id}-{ordinal}-{frame.DifferenceHash}.jpg");
        var image = await ThumbnailBitmapLoader.LoadRemoteAsync(services.RemoteThumbnails, key,
            async cancellation => File.Exists(legacy) ? File.OpenRead(legacy)
                : (frame.Id > 0 ? await gateway.OpenVideoKeyframeAsync(frame.Id, cancellation) : null)
                    ?? await gateway.OpenVideoKeyframeByOrdinalAsync(video.Id, ordinal, cancellation), token, prefetch);
        if (image is not null && File.Exists(legacy))
            try { File.Delete(legacy); }
            catch (IOException exception) { Picer.Core.Logging.AppLogger.Warn($"Legacy remote keyframe cleanup deferred; video={video.Id}; frame={frame.Id}; type={exception.GetType().Name}"); }
            catch (UnauthorizedAccessException exception) { Picer.Core.Logging.AppLogger.Warn($"Legacy remote keyframe cleanup deferred; video={video.Id}; frame={frame.Id}; type={exception.GetType().Name}"); }
        return image is null ? null : services.RemoteThumbnails.TryGet(key);
    }

    public static async Task<LegacyMigrationResult> MigrateLegacyCacheAsync(ClientServices services,
        IReadOnlyDictionary<long, (VideoRecord Video, VideoKeyframe[] Frames)> metadata, CancellationToken token)
    {
        var root = Path.Combine(services.RemoteVideoCacheRoot ?? AppServices.RemoteDataRoot, "remote-video-keyframes");
        if (!Directory.Exists(root) || services.Gateway is not HttpIndexGateway) return new(0, 0, 0);

        var migrated = 0;
        var discarded = 0;
        var failed = 0;
        using var concurrency = new SemaphoreSlim(3, 3);
        var work = new List<Task>();
        foreach (var videoDirectory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            var folderName = Path.GetFileName(videoDirectory);
            if (!long.TryParse(folderName, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var videoId))
            {
                discarded += DeleteLegacyFiles(videoDirectory);
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(videoDirectory, "*.jpg", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                var parts = Path.GetFileNameWithoutExtension(path).Split('-');
                if (!metadata.TryGetValue(videoId, out var entry) || parts.Length != 4 || parts[0] != "v2" ||
                    !long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var frameId) ||
                    !int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ordinal) ||
                    !long.TryParse(parts[3], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var differenceHash) ||
                    ordinal < 0 || ordinal >= entry.Frames.Length || entry.Frames[ordinal] is not { } frame ||
                    frame.Id != frameId || frame.DifferenceHash != differenceHash)
                {
                    try { File.Delete(path); discarded++; }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failed++; }
                    continue;
                }

                var capturedPath = path;
                var capturedFrame = frame;
                var capturedVideo = entry.Video;
                work.Add(MigrateOneAsync(capturedPath, capturedVideo, capturedFrame, ordinal));
            }
        }
        await Task.WhenAll(work).ConfigureAwait(false);
        return new(migrated, discarded, failed);

        async Task MigrateOneAsync(string path, VideoRecord video, VideoKeyframe frame, int ordinal)
        {
            await concurrency.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!File.Exists(path)) return;
                var cached = await GetAsync(services, video, frame, ordinal, token).ConfigureAwait(false);
                if (cached is not null && !File.Exists(path)) Interlocked.Increment(ref migrated);
                else Interlocked.Increment(ref failed);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException or UnauthorizedAccessException)
            { Interlocked.Increment(ref failed); }
            finally { concurrency.Release(); }
        }

        static int DeleteLegacyFiles(string directory)
        {
            var deleted = 0;
            foreach (var path in Directory.EnumerateFiles(directory, "*.jpg", SearchOption.TopDirectoryOnly))
                try { File.Delete(path); deleted++; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            return deleted;
        }
    }
}
