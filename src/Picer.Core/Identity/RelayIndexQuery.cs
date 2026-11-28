using System.Text.Json;

namespace Picer.Core.Identity;

/// <summary>Method, gateway path and optional JSON body travel inside the authenticated ciphertext.</summary>
public sealed record RelayIndexQuery(string Method, string Path, JsonElement? Body = null)
{
    public static bool IsSupported(string method, string path) => method switch
    {
        "GET" => path is "health" or "api/gateway/v1/sources" or "api/gateway/v1/directories"
            or "api/gateway/v1/albums" or "api/gateway/v1/tags" or "api/gateway/v1/browser-metadata"
            || TryPhoto(path, out _, out _) || TryAsset(path, out _, out _) || TryRecord(path, out _, out _),
        "POST" => path is "api/gateway/v1/search" or "api/gateway/v1/video-search" or LeasePath,
        "HEAD" => TryAsset(path, out _, out _),
        _ => false
    };

    public const string LeasePath = "api/gateway/v1/media-lease";
    public static bool TryAsset(string path, out long id, out bool video) =>
        TryMediaId(path, "api/gateway/v1/assets/images/", "api/gateway/v1/assets/videos/", out id, out video) && id > 0;

    public static bool TryRecord(string path, out long id, out bool video) =>
        TryMediaId(path, "api/gateway/v1/images/", "api/gateway/v1/videos/", out id, out video);

    private static bool TryMediaId(string path, string images, string videos, out long id, out bool video)
    {
        video = path.StartsWith(videos, StringComparison.Ordinal);
        id = 0;
        return (video || path.StartsWith(images, StringComparison.Ordinal)) && long.TryParse(path.AsSpan(video ? videos.Length : images.Length),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id);
    }

    public static bool TryPhoto(string path, out long id, out bool preview)
    {
        const string thumbnails = "api/gateway/v1/thumbs/";
        const string previews = "api/gateway/v1/previews/";
        preview = path.StartsWith(previews, StringComparison.Ordinal);
        id = 0;
        if (!preview && !path.StartsWith(thumbnails, StringComparison.Ordinal)) return false;
        return long.TryParse(path.AsSpan(preview ? previews.Length : thumbnails.Length), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out id) && id > 0;
    }
}
