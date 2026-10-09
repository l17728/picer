using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.Core.Vision;

public sealed record DirectoryMediaDescriptionPlan(string Directory, int TotalPhotos, int TotalVideos,
    IReadOnlyList<ImageRecord> Photos, IReadOnlyList<VideoRecord> Videos, int FailedCount = 0, int DeferredCount = 0)
{
    public int PendingCount => Photos.Count + Videos.Count;
    public int SkippedCount => TotalPhotos + TotalVideos - PendingCount - FailedCount - DeferredCount;

    public static bool Contains(string directory, string path) => Picer.Core.Indexing.DirectoryMediaImport.Normalize(path)
        .StartsWith(Picer.Core.Indexing.DirectoryMediaImport.Normalize(directory) + "/", StringComparison.OrdinalIgnoreCase);

    public static DirectoryMediaDescriptionPlan Build(PicerDatabase database, CloudVisionRepository photos,
        VideoRepository videos, string directory, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("请选择目录树节点。", nameof(directory));
        var imagePlan = DirectoryDescriptionPlan.Build(database, photos, directory, token: token);
        var videoRows = database.GetVideosInDirectory(directory, token);
        return new(directory, imagePlan.Total, videoRows.Count, imagePlan.Pending,
            videoRows.Where(row => !row.Described).Select(row => row.Video).ToArray());
    }
}
