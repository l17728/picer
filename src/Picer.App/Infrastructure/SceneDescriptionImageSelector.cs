using Picer.Core.Models;

namespace Picer.App.Infrastructure;

internal static class SceneDescriptionImageSelector
{
    internal static ImageRecord[] SelectReadable(IEnumerable<ImageRecord?> records, bool serviceMode,
        Func<string, bool> localFileExists)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(localFileExists);

        return records
            .Where(image => image is { Status: ImageStatus.Active } &&
                (serviceMode || localFileExists(image.Path)))
            .Cast<ImageRecord>()
            .ToArray();
    }
}
