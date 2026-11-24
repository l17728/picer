using System.Text.Json.Nodes;

namespace Picer.MediaAgent;

/// <summary>
/// Face model endpoints take the image itself and never a path. Service tasks carry the indexed
/// path of a photo or keyframe, so the agent reads that file inside its configured roots and
/// replaces the path with the encoded image before the request leaves the machine.
/// </summary>
public static class FaceImagePayload
{
    /// <summary>The largest photo that is forwarded; the model service rejects larger images.</summary>
    public const long MaxImageBytes = 10L * 1024 * 1024;

    /// <summary>
    /// Returns the payload with <c>imageBase64</c> filled from <c>path</c>. A payload that already
    /// carries an image is returned unchanged. <paramref name="resolvePath"/> maps an indexed path,
    /// such as the administrative share of the source machine, to a configured local path; the
    /// root check is applied to its result, so it cannot widen the readable set.
    /// </summary>
    public static async Task<string> HydrateAsync(string payload, IReadOnlyList<string> roots,
        Func<string, string> resolvePath, CancellationToken cancellationToken)
    {
        var document = JsonNode.Parse(payload)?.AsObject()
            ?? throw new InvalidOperationException("face_request_invalid");
        if (document["imageBase64"]?.GetValue<string>() is { Length: > 0 }) return document.ToJsonString();
        var path = document["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("face_image_required");
        var fullPath = Path.GetFullPath(resolvePath(path.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInsideRoots(fullPath, roots)) throw new UnauthorizedAccessException("face image is outside configured roots");
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length == 0 || info.Length > MaxImageBytes)
            throw new InvalidDataException("face image is missing or exceeds the 10 MiB service limit");
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        document.Remove("path");
        document["imageBase64"] = Convert.ToBase64String(bytes);
        return document.ToJsonString();
    }

    private static bool IsInsideRoots(string fullPath, IReadOnlyList<string> roots) => roots.Any(root =>
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    });
}
