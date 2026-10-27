using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;

namespace Picer.Tests;

public sealed class MetadataReplayIdempotenceTests
{
    [Fact]
    public void BoundedAcknowledgmentPreservesUnrelatedPendingEditsForExplicitReplay()
    {
        using var db = new PicerDatabase(":memory:");
        db.UpsertMirroredPerson(new(1, "first", 0, 0));
        db.UpsertMirroredPerson(new(2, "second", 0, 0));
        using (var transaction = db.BeginTransaction())
        {
            db.RecordMetadataEdit("person", 2, "name", "pending", transaction);
            transaction.Commit();
        }
        db.MergeMetadataEdits([new("person", 1, "name", "acknowledged", DateTime.UtcNow.ToString("O"), Guid.NewGuid().ToString("N"))], applyOnlyChangedFields: true);
        using var name = db.Command("SELECT display_name FROM people WHERE id=2");
        Assert.Equal("second", name.ExecuteScalar());
        Assert.Equal(2, db.GetMetadataEdits().Count);
        db.ReplayMetadataEdits();
        Assert.Equal("pending", name.ExecuteScalar());
    }

    [Fact]
    public void ReplayingDifferentPersonFieldsDoesNotDirtyTheMirrorAgain()
    {
        using var database=new PicerDatabase(":memory:");
        database.UpsertMirroredPerson(new(1,"original",0,0));
        MetadataEdit[] edits=[new("person",1,"name","corrected","2030-01-01T00:00:00.0000000Z",Guid.NewGuid().ToString("N")),
            new("person",1,"description","description","2030-01-02T00:00:00.0000000Z",Guid.NewGuid().ToString("N")),
            new("person",1,PicerDatabase.AliasField("alias"),"alias","2030-01-03T00:00:00.0000000Z",Guid.NewGuid().ToString("N"))];
        database.MergeMetadataEdits(edits);
        using var revision=database.Command("SELECT metadata_revision FROM mirror_revision WHERE id=1");
        var before=revision.ExecuteScalar();
        database.MergeMetadataEdits(edits);
        database.ReplayMetadataEdits();
        Assert.Equal(before,revision.ExecuteScalar());
        using var person=database.Command("SELECT display_name,description FROM people WHERE id=1");
        using var row=person.ExecuteReader(); Assert.True(row.Read());
        Assert.Equal("corrected",row.GetString(0)); Assert.Equal("description",row.GetString(1));
    }

    [Fact]
    public void ReplayingIgnoredFaceDoesNotCreateANewMetadataRevision()
    {
        using var database=new PicerDatabase(":memory:"); var source=database.AddSource(@"C:\Photos");
        database.UpsertMirroredImage(new ImageRecord { Id=1,SourceId=source.Id,Path=@"C:\Photos\one.jpg",FileSize=10,
            ModifiedUtc=DateTime.UtcNow,IndexedUtc=DateTime.UtcNow });
        database.UpsertMirroredFace(new(1,1,"model",0,0,0,10,10,0.9,null,null,DateTime.UtcNow.ToString("O")));
        GatewayFaceReviewDecision[] decisions=[new(1,"ignored","2030-01-01T00:00:00.0000000Z")];
        database.MergeFaceReviewDecisions(decisions);
        using var revision=database.Command("SELECT metadata_revision FROM mirror_revision WHERE id=1");
        var before=revision.ExecuteScalar(); database.MergeFaceReviewDecisions(decisions);
        Assert.Equal(before,revision.ExecuteScalar());
        Assert.Equal(decisions, database.GetFaceReviewDecisions());
    }
}
