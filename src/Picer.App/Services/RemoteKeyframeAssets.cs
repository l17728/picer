using System.IO;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.App.Services;

internal static class RemoteKeyframeAssets
{
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
        return image is null ? null : services.RemoteThumbnails.TryGet(key);
    }
}
