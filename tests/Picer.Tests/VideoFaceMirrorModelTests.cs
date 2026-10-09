using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class VideoFaceMirrorModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "video-face-mirror-model-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    public VideoFaceMirrorModelTests()
    {
        _db = new(Path.Combine(_root, "test.db"));
        var source = _db.AddSource(_root).Id;
        new VideoRepository(_db).UpsertMirrored(new(42, source, Path.Combine(_root, "clip.mp4"), 3,
            DateTime.UnixEpoch, null, 0, 0, null, null, "active"), source);
    }

    private static GatewayVideoFace Face(long id, string model, byte[]? embedding) =>
        new(id, 42, 11, 0, 0, 0, 0, 1, 1, 1, embedding, null, null, null, "ignored", null, "2026-01-01")
        { ModelVersion = model };

    private void Apply(params GatewayVideoFace[] faces) => _db.UpsertMirroredVideoMetadata(
        new([], [], [], [], [], [new(11, 42, 0, "frame.jpg", 0)], [], [], faces), authoritative: true);

    [Fact]
    public void SameFrameAndOrdinalAcrossModelsImportsAtomicallyAndPreservesOmittedVectors()
    {
        Apply(Face(101, "model-a", [1, 2, 3, 4]), Face(102, "model-b", [5, 6, 7, 8]));
        Apply(Face(101, "model-a", null), Face(102, "model-b", null));
        var faces = _db.GetAllMirroredVideoFaces().OrderBy(face => face.Id).ToArray();
        Assert.Equal(2, faces.Length);
        Assert.Equal("model-a", faces[0].ModelVersion); Assert.Equal(new byte[] { 1, 2, 3, 4 }, faces[0].Embedding);
        Assert.Equal("model-b", faces[1].ModelVersion); Assert.Equal(new byte[] { 5, 6, 7, 8 }, faces[1].Embedding);
        Assert.All(faces, face => Assert.Equal("ignored", face.AssignmentState));
        using var check = _db.Command("PRAGMA foreign_key_check"); Assert.Null(check.ExecuteScalar());
    }

    [Fact]
    public void ChangedModelClearsPreviousVectorButLegacyPayloadKeepsKnownModel()
    {
        Apply(Face(101, "model-a", [1, 2, 3, 4]));
        Apply(Face(101, "", null));
        var legacy = Assert.Single(_db.GetAllMirroredVideoFaces());
        Assert.Equal("model-a", legacy.ModelVersion); Assert.NotNull(legacy.Embedding);
        Apply(Face(101, "model-b", null));
        var changed = Assert.Single(_db.GetAllMirroredVideoFaces());
        Assert.Equal("model-b", changed.ModelVersion); Assert.Null(changed.Embedding);
    }

    public void Dispose()
    { _db.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
