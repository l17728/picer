using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed record DirectoryDescriptionPlan(int Total, int AlreadyDescribed, IReadOnlyList<ImageRecord> Pending)
{
    public static DirectoryDescriptionPlan Build(PicerDatabase database, CloudVisionRepository descriptions,
        string directory, long? sourceId = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("请选择实际目录。", nameof(directory));
        var images = database.GetImagesInDirectory(directory, sourceId, token);
        var described = descriptions.GetCurrentDescriptionIds(images, token);
        var pending = images.Where(image => !described.Contains(image.Id)).ToArray();
        return new(images.Count, images.Count - pending.Length, pending);
    }
}
