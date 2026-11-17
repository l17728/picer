using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Picer.Core.Gateway;
using Picer.Core.Video;

namespace Picer.App.Services;

internal static class VideoDetailsCache
{
    private static string PathFor(string root, VideoRecord video, string model, string epoch)
    {
        var identity = $"{epoch}:{video.Id}:{video.Sha256}:{video.FileSize}:{video.ModifiedUtc.Ticks}:{model}";
        return Path.Combine(root, "video-details", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }
    public static async Task<VideoDetailsSnapshot?> ReadAsync(string root, VideoRecord video, string model, string epoch, CancellationToken token)
    {
        try
        {
            var path = PathFor(root, video, model, epoch);
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) return null;
            await using var input = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<VideoDetailsSnapshot>(input, cancellationToken: token);
            return value is not null && Matches(value, video, epoch) ? value : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException) { return null; }
    }
    public static bool Matches(VideoDetailsSnapshot value, VideoRecord video, string epoch) => value.Epoch == epoch
        && value.Video.Id == video.Id && value.Video.FileSize == video.FileSize && value.Video.ModifiedUtc == video.ModifiedUtc
        && string.Equals(value.Video.Sha256, video.Sha256, StringComparison.OrdinalIgnoreCase);
    public static async Task SaveAsync(string root, string model, VideoDetailsSnapshot value, CancellationToken token)
    {
        var path = PathFor(root, value.Video, model, value.Epoch);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var output = File.Create(temporary)) await JsonSerializer.SerializeAsync(output, value, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
