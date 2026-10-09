using System.Globalization;

namespace Picer.Core.Video;

internal static class VideoMediaCachePaths
{
    internal static async Task<string> DirectoryAsync(string cacheRoot, VideoRecord video, bool local,
        CancellationToken token)
    {
        var identity = video.Sha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : null;
        if (local)
        {
            identity = await LocalVideoContentHashCache.Shared.GetHashAsync(video.Path, token).ConfigureAwait(false);
        }
        // Remote legacy records without a content signature cannot safely reuse
        // a prior poster, even when size and mtime happen to be unchanged.
        identity ??= "generation-" + Guid.NewGuid().ToString("N");
        return Path.Combine(cacheRoot, video.Id.ToString(CultureInfo.InvariantCulture), identity);
    }
}
