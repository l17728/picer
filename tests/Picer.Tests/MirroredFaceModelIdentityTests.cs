using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;

namespace Picer.Tests;

public sealed class MirroredFaceModelIdentityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ModelOrPhotoChangeDoesNotRetainOldEmbedding(bool batch, bool changePhoto)
    {
        using var database = new PicerDatabase(":memory:");
        var source = database.AddSource(@"C:\Photos");
        for (var id = 1; id <= 2; id++) database.UpsertMirroredImage(new ImageRecord { Id = id, SourceId = source.Id,
            Path = $@"C:\Photos\{id}.jpg", FileSize = 10, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow });
        var face = new GatewayDetectedFace(1, 1, "model-a", 0, 0, 0, 10, 10, 0.9, null, [1, 2, 3], DateTime.UtcNow.ToString("O"));
        void Apply(GatewayDetectedFace value)
        {
            if (batch) database.UpsertMirroredFaces([value], []);
            else database.UpsertMirroredFace(value);
        }
        Apply(face);
        Apply(face with { Embedding = null });
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(database.GetAllMirroredFaces()).Embedding);
        var changed = face with { ImageId = changePhoto ? 2 : 1, ModelVersion = changePhoto ? "model-a" : "model-b", Embedding = null };
        Apply(changed);
        Assert.Null(Assert.Single(database.GetAllMirroredFaces()).Embedding);
        Apply(changed with { Embedding = [4, 5, 6] });
        Apply(changed);
        Assert.Equal(new byte[] { 4, 5, 6 }, Assert.Single(database.GetAllMirroredFaces()).Embedding);
    }
}
