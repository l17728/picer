using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.SemanticSearch;

namespace Picer.Tests;

public sealed class ClipModelIsolationTests
{
    [Fact]
    public void Search_ExcludesEmbeddingsFromPreviousImagePreprocessing()
    {
        using var database = new PicerDatabase(":memory:");
        var source = database.AddSource(Path.GetTempPath());
        var oldImage = database.UpsertImage(new ImageRecord
        {
            SourceId = source.Id, Path = Path.Combine(source.Path, "old.png"),
            FileSize = 1, ModifiedUtc = DateTime.UtcNow
        });
        var currentImage = database.UpsertImage(new ImageRecord
        {
            SourceId = source.Id, Path = Path.Combine(source.Path, "current.png"),
            FileSize = 1, ModifiedUtc = DateTime.UtcNow
        });
        var repository = new ClipSemanticRepository(database);
        const string previous = "OFA-Chinese-CLIP-ViT-B-16/Xenova-f269048-int8";
        repository.Upsert(oldImage, previous, new float[] { 1, 0 });
        repository.Upsert(currentImage, ChineseClipEncoder.ModelVersion, new float[] { 1, 0 });
        Assert.False(repository.IsCurrent(oldImage, ChineseClipEncoder.ModelVersion));
        Assert.Equal(currentImage.Id, Assert.Single(repository.Search(new float[] { 1, 0 })).Image.Id);
        Assert.Equal(oldImage.Id, Assert.Single(repository.Search(new float[] { 1, 0 }, modelVersion: previous)).Image.Id);
    }
}
