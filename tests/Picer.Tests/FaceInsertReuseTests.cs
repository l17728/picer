using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.People;

namespace Picer.Tests;

public sealed class FaceInsertReuseTests
{
    [Fact]
    public void ReusedFaceStatementsRespectRollbackNewTransactionsAndAutocommit()
    {
        using var database = new PicerDatabase(":memory:");
        var source = database.AddSource(@"C:\Photos");
        var image = database.UpsertImage(new ImageRecord { SourceId = source.Id, Path = @"C:\Photos\one.jpg",
            FileSize = 1, ModifiedUtc = DateTime.UnixEpoch, IndexedUtc = DateTime.UtcNow });
        var person = new PeopleService(database).Create("test-person");
        var face = new GatewayDetectedFace(1, image.Id, "model-a", 0, 0, 0, 1, 1, 0.9, null,
            BitConverter.GetBytes(1f), DateTime.UtcNow.ToString("O"));
        var assignment = new GatewayFaceAssignment(1, person.Id, "suggested", 0.9, "cluster", DateTime.UtcNow.ToString("O"));
        using (var transaction = database.BeginTransaction())
        {
            database.UpsertMirroredFace(face, transaction);
            database.UpsertMirroredFaceAssignment(assignment, transaction);
            transaction.Rollback();
        }
        using (var transaction = database.BeginTransaction())
        {
            database.UpsertMirroredFace(face with { ModelVersion = "model-b" }, transaction);
            database.UpsertMirroredFaceAssignment(assignment, transaction);
            transaction.Commit();
        }
        database.UpsertMirroredFace(face with { ModelVersion = "model-b", Embedding = null });
        database.UpsertMirroredFaceAssignment(assignment);
        using var query = database.Command("SELECT model_version,embedding FROM detected_faces WHERE id=1");
        using (var row = query.ExecuteReader())
        {
            Assert.True(row.Read()); Assert.Equal("model-b", row.GetString(0));
            Assert.Equal(BitConverter.GetBytes(1f), (byte[])row[1]);
        }
        database.UpsertMirroredFace(face with { ModelVersion = "model-c", Embedding = null });
        using var changed = query.ExecuteReader(); Assert.True(changed.Read()); Assert.True(changed.IsDBNull(1));
    }
}
