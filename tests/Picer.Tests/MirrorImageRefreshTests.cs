using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Tests;

public sealed class MirrorImageRefreshTests
{
    [Fact]
    public void SameCountRefreshAppliesPathTimestampAndMissingStatusChangesById()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-mirror-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new PicerDatabase(Path.Combine(root, "mirror.db"));
            var source = database.AddSource(root);
            var original = new ImageRecord { Id = 200, SourceId = source.Id, Path = Path.Combine(root, "old.jpg"),
                FileSize = 10, ModifiedUtc = DateTime.UtcNow.AddDays(-1), Status = ImageStatus.Active };
            database.UpsertMirroredImageBatch([original]);
            var incoming = new ImageRecord { Id = original.Id, SourceId = source.Id, Path = Path.Combine(root, "moved.jpg"),
                FileSize = 15, ModifiedUtc = original.ModifiedUtc.AddHours(2), Status = ImageStatus.Missing };

            database.UpsertMirroredImageBatch([incoming]);

            var refreshed = Assert.Single(database.GetImages(includeMissing: true));
            Assert.Equal(original.Id, refreshed.Id);
            Assert.Equal(incoming.Path, refreshed.Path);
            Assert.Equal(incoming.ModifiedUtc, refreshed.ModifiedUtc);
            Assert.Equal(15, refreshed.FileSize);
            Assert.Equal(ImageStatus.Missing, refreshed.Status);
            Assert.Null(database.GetImageByPath(original.Path, anyStatus: true));
        }
        finally { Directory.Delete(root, true); }
    }
}
