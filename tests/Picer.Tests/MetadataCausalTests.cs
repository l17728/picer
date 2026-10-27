using Picer.Core.Data;

namespace Picer.Tests;

public sealed class MetadataCausalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoteResolutionReceiptWaitsForDataAndPreservesNewOfflineEdits(bool useProposed)
    {
        using var server=Database(); using var source=Database();
        var initial=Assert.Single(server.AcceptMetadataEdits([Edit("initial")]));
        source.MergeMetadataEdits([initial]);
        var pending=Change(source,"source");
        var remote=Assert.Single(server.AcceptMetadataEdits([Edit("remote",initial.ServerVersion)]));
        server.AcceptMetadataEdits([pending]); source.MergeMetadataEdits([remote]);
        var snapshot=server.GetMetadataConflictSnapshot();
        server.ResolveMetadataEditConflict(pending.OperationId,useProposed,remote.ServerVersion,remote.OperationId,true,snapshot.Epoch);
        var resolved=Assert.Single(server.GetMetadataEdits());
        using var query=server.Command("SELECT resolved_version FROM metadata_edit_conflicts WHERE operation_id=$op");
        query.Parameters.AddWithValue("$op",pending.OperationId);
        var receipt=new MetadataConflictReceipt(pending.OperationId,Convert.ToInt64(query.ExecuteScalar()));
        Assert.Equal(resolved.ServerVersion,receipt.Version);
        bool Apply()
        {
            using var tx=source.BeginTransaction(); var applied=source.ApplyMetadataConflictReceipt(receipt,tx); tx.Commit(); return applied;
        }
        if (useProposed) { Assert.False(Apply()); Assert.Single(source.GetMetadataEditConflicts()); }
        var newer=Change(source,"new offline choice");
        Assert.False(Apply()); Assert.Equal(newer,Assert.Single(source.GetMetadataEdits()));
        source.MergeMetadataEdits([resolved]);
        // Keeping the old winner cannot replace a newer local pending operation.
        if (!useProposed)
        {
            Assert.False(Apply());
            var accepted=Assert.Single(server.AcceptMetadataEdits([newer]));
            source.MergeMetadataEdits([accepted]);
        }
        Assert.True(Apply()); Assert.False(Apply());
        Assert.DoesNotContain(source.GetMetadataEditConflicts(),conflict=>conflict.OperationId==pending.OperationId);
        if (useProposed) Assert.Contains(source.GetMetadataEditConflicts(),conflict=>conflict.OperationId==newer.OperationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoritativeConflictReviewChecksEpochAndRetriesWithoutChangingTheDecision(bool useProposed)
    {
        using var database = Database();
        var current = Assert.Single(database.AcceptMetadataEdits([Edit("current")]));
        database.AcceptMetadataEdits([Edit("proposed")]);
        var snapshot = database.GetMetadataConflictSnapshot();
        var conflict = Assert.Single(snapshot.Reviews).Conflict;
        Assert.Throws<InvalidOperationException>(() => database.ResolveMetadataEditConflict(conflict.OperationId,useProposed,
            current.ServerVersion,current.OperationId,authoritative:true,expectedEpoch:Guid.NewGuid().ToString("N")));
        database.ResolveMetadataEditConflict(conflict.OperationId,useProposed,current.ServerVersion,current.OperationId,
            authoritative:true,expectedEpoch:snapshot.Epoch);
        var resolved = Assert.Single(database.GetMetadataEdits());
        Assert.Equal(useProposed ? "proposed" : "current",resolved.Value);
        Assert.Equal(useProposed,resolved.ServerVersion > current.ServerVersion);
        database.ResolveMetadataEditConflict(conflict.OperationId,useProposed,current.ServerVersion,current.OperationId,
            authoritative:true,expectedEpoch:snapshot.Epoch);
        Assert.Equal(resolved,Assert.Single(database.GetMetadataEdits()));
        Assert.Throws<InvalidOperationException>(() => database.ResolveMetadataEditConflict(conflict.OperationId,!useProposed,
            current.ServerVersion,current.OperationId,authoritative:true,expectedEpoch:snapshot.Epoch));
        Assert.Empty(database.GetMetadataEditConflicts());
    }

    [Fact]
    public void AliasRemovalIsVersionedAndOldCopiesCannotRestoreItButAnExplicitAddCan()
    {
        using var server = Database(); using var source = Database();
        var people = new Picer.Core.People.PeopleService(source);
        people.AddAlias(1, "旅伴");
        var original = Assert.Single(source.GetMetadataEdits());
        var accepted = Assert.Single(server.AcceptMetadataEdits([original]));
        source.MergeMetadataEdits([accepted]);
        people.RemoveAlias(1, "旅伴");
        var removal = Assert.Single(source.GetMetadataEdits());
        Assert.Equal(string.Empty, removal.Value); Assert.Equal(accepted.ServerVersion, removal.BaseVersion);
        var removed = Assert.Single(server.AcceptMetadataEdits([removal]));
        source.MergeMetadataEdits([removed]);
        Assert.DoesNotContain("旅伴", new Picer.Core.People.PeopleService(server).Get(1)!.Aliases);
        Assert.Equal(removed, Assert.Single(server.AcceptMetadataEdits([original with { OperationId = Guid.NewGuid().ToString("N") }])));
        using (var staleSnapshot = server.Command("INSERT INTO person_aliases(person_id,alias,created_utc) VALUES(1,'旅伴',$now)"))
        { staleSnapshot.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O")); staleSnapshot.ExecuteNonQuery(); }
        server.ReplayMetadataEdits();
        Assert.DoesNotContain("旅伴", new Picer.Core.People.PeopleService(server).Get(1)!.Aliases);
        people.AddAlias(1, "旅伴");
        var restore = Assert.Single(source.GetMetadataEdits());
        Assert.Equal(removed.ServerVersion, restore.BaseVersion);
        Assert.Equal("旅伴", Assert.Single(server.AcceptMetadataEdits([restore])).Value);
        Assert.Contains("旅伴", new Picer.Core.People.PeopleService(server).Get(1)!.Aliases);
    }

    private static PicerDatabase Database()
    {
        var database = new PicerDatabase(":memory:");
        database.UpsertMirroredPerson(new(1,"initial",0,0));
        return database;
    }
    private static MetadataEdit Edit(string value, long version=0, string field="name") =>
        new("person",1,field,value,"2030-01-01T00:00:00.0000000Z",Guid.NewGuid().ToString("N")) { BaseVersion=version };
    private static MetadataEdit Change(PicerDatabase database,string value)
    {
        using var transaction=database.BeginTransaction();
        database.RecordMetadataEdit("person",1,"name",value,transaction);
        database.ReplayMetadataEditsInTransaction(transaction); transaction.Commit();
        return Assert.Single(database.GetMetadataEdits());
    }

    [Fact]
    public void ConcurrentFieldEditsKeepBothValuesAndResolutionUsesLatestVersion()
    {
        using var server=Database(); using var source=Database();
        var initial=Assert.Single(server.AcceptMetadataEdits([Edit("baseline")]));
        source.MergeMetadataEdits([initial]);
        var pending=Change(source,"source choice");
        var remote=Assert.Single(server.AcceptMetadataEdits([Edit("remote choice",initial.ServerVersion)]));
        var stale=Assert.Single(server.AcceptMetadataEdits([pending]));
        Assert.Equal(remote,stale);
        source.MergeMetadataEdits([stale]);
        var conflict=Assert.Single(source.GetMetadataEditConflicts());
        Assert.Equal("source choice",conflict.Proposed.Value); Assert.Equal("remote choice",conflict.Current.Value);
        source.ResolveMetadataEditConflict(conflict.OperationId,true,conflict.Current.ServerVersion,conflict.Current.OperationId);
        var resolved=Assert.Single(source.GetMetadataEdits()); Assert.Equal(remote.ServerVersion,resolved.BaseVersion);
        Assert.Equal("source choice",Assert.Single(server.AcceptMetadataEdits([resolved])).Value);
        Assert.Empty(source.GetMetadataEditConflicts());
    }

    [Fact]
    public void ClockSkewDoesNotOverrideVersionsAndPendingChangeSurvivesOldBaseline()
    {
        using var server=Database(); using var source=Database();
        var initial=Assert.Single(server.AcceptMetadataEdits([Edit("first") with { UpdatedUtc="2050-01-01T00:00:00.0000000Z" }]));
        source.MergeMetadataEdits([initial]); var pending=Change(source,"pending");
        source.MergeMetadataEdits([initial]); Assert.Equal(pending,Assert.Single(source.GetMetadataEdits()));
        Assert.Empty(source.GetMetadataEditConflicts());
        var next=Assert.Single(server.AcceptMetadataEdits([Edit("second",initial.ServerVersion) with { UpdatedUtc="2000-01-01T00:00:00.0000000Z" }]));
        Assert.True(next.ServerVersion>initial.ServerVersion); Assert.Equal("second",next.Value);
        source.MergeMetadataEdits([next]); Assert.Single(source.GetMetadataEditConflicts());
        source.MergeMetadataEdits([initial]); Assert.Equal("second",Assert.Single(source.GetMetadataEdits()).Value);
    }

    [Fact]
    public void ReceiptsAreIdempotentAndReusedOperationCannotChangePayload()
    {
        using var server=Database(); var request=Edit("first");
        var first=Assert.Single(server.AcceptMetadataEdits([request]));
        Assert.Equal(first,Assert.Single(server.AcceptMetadataEdits([request])));
        var second=Assert.Single(server.AcceptMetadataEdits([Edit("second",first.ServerVersion)]));
        Assert.Equal(first,Assert.Single(server.AcceptMetadataEdits([request])));
        Assert.Equal(second,Assert.Single(server.AcceptMetadataEdits([first])));
        Assert.Throws<InvalidDataException>(()=>server.AcceptMetadataEdits([request with { Value="forged" }]));
        Assert.Equal(second,Assert.Single(server.GetMetadataEdits()));
    }

    [Fact]
    public void IndependentFieldsMergeAndConflictResolutionRejectsChangedReview()
    {
        using var server=Database(); using var source=Database();
        var first=Assert.Single(server.AcceptMetadataEdits([Edit("first")])); source.MergeMetadataEdits([first]);
        var pending=Change(source,"local");
        var other=server.AcceptMetadataEdits([Edit("remote",first.ServerVersion),Edit("description",field:"description")]);
        Assert.Equal(2,other.Count); source.MergeMetadataEdits(other);
        var conflict=Assert.Single(source.GetMetadataEditConflicts());
        var newer=Assert.Single(server.AcceptMetadataEdits([Edit("latest",conflict.Current.ServerVersion)]));
        source.MergeMetadataEdits([newer]);
        Assert.Throws<InvalidOperationException>(()=>source.ResolveMetadataEditConflict(pending.OperationId,true,conflict.Current.ServerVersion,conflict.Current.OperationId));
        source.ResolveMetadataEditConflict(pending.OperationId,false,newer.ServerVersion,newer.OperationId);
        Assert.Empty(source.GetMetadataEditConflicts());
    }

    [Fact]
    public void PurgingEntityRemovesItsConflictAndReceiptPayloads()
    {
        using var server=Database();
        server.AcceptMetadataEdits([Edit("one")]); server.AcceptMetadataEdits([Edit("two")]);
        Assert.Single(server.GetMetadataEditConflicts());
        using var remove=server.Command("DELETE FROM people WHERE id=1"); remove.ExecuteNonQuery();
        Assert.Empty(server.GetMetadataEditConflicts());
        using var count=server.Command("SELECT COUNT(*) FROM metadata_causal_receipts"); Assert.Equal(0L,count.ExecuteScalar());
    }

    [Fact]
    public void ConflictReviewCannotOverwriteANewerOfflineEditWithTheSameBaseVersion()
    {
        using var server=Database(); using var source=Database();
        var first=Assert.Single(server.AcceptMetadataEdits([Edit("first")])); source.MergeMetadataEdits([first]);
        Change(source,"pending");
        source.MergeMetadataEdits(server.AcceptMetadataEdits([Edit("remote",first.ServerVersion)]));
        var reviewed=Assert.Single(source.GetMetadataEditConflicts());
        var newer=Change(source,"new offline choice");
        Assert.Equal(reviewed.Current.ServerVersion,newer.BaseVersion);
        Assert.Throws<InvalidOperationException>(()=>source.ResolveMetadataEditConflict(reviewed.OperationId,true,
            reviewed.Current.ServerVersion,reviewed.Current.OperationId));
        Assert.Equal(newer,Assert.Single(source.GetMetadataEdits()));
    }

    [Fact]
    public void ExplicitLinkRemovalCannotBeResurrectedByAnOlderClockOrSnapshot()
    {
        using var server=Database();
        var source=server.AddSource(Path.GetTempPath());
        server.UpsertMirroredImage(new Picer.Core.Models.ImageRecord { Id=1,SourceId=source.Id,Path=Path.Combine(source.Path,"causal.jpg"),
            FileSize=10,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UnixEpoch });
        var tag=Picer.Core.Search.SemanticEngine.AddTag(server,"test").Id;
        var add=new MetadataEdit("tag-link",1,tag.ToString(System.Globalization.CultureInfo.InvariantCulture),"1",
            "2030-01-01T00:00:00.0000000Z",Guid.NewGuid().ToString("N"));
        var initial=Assert.Single(server.AcceptMetadataEdits([add]));
        var remove=Assert.Single(server.AcceptMetadataEdits([add with { Value="0",OperationId=Guid.NewGuid().ToString("N"),BaseVersion=initial.ServerVersion }]));
        var stale=Assert.Single(server.AcceptMetadataEdits([add with { UpdatedUtc="2099-01-01T00:00:00.0000000Z",OperationId=Guid.NewGuid().ToString("N") }]));
        Assert.Equal(remove,stale);
        using var oldSnapshot=server.Command("INSERT OR IGNORE INTO image_tags(image_id,tag_id) VALUES(1,$tag)");
        oldSnapshot.Parameters.AddWithValue("$tag",tag); oldSnapshot.ExecuteNonQuery();
        server.ReplayMetadataEdits();
        using var count=server.Command("SELECT COUNT(*) FROM image_tags WHERE image_id=1"); Assert.Equal(0L,count.ExecuteScalar());
        Assert.Single(server.GetMetadataEditConflicts());
    }

    [Fact]
    public void InvalidBatchRollsBackBusinessVersionsAndReceiptsTogether()
    {
        using var server=Database();
        Assert.Throws<ArgumentException>(()=>server.AcceptMetadataEdits([Edit("valid"),Edit("invalid") with { OperationId="not-an-id" }]));
        Assert.Empty(server.GetMetadataEdits());
        using var clock=server.Command("SELECT version FROM metadata_version_clock WHERE id=1"); Assert.Equal(0L,clock.ExecuteScalar());
        using var receipts=server.Command("SELECT COUNT(*) FROM metadata_causal_receipts"); Assert.Equal(0L,receipts.ExecuteScalar());
    }

    [Fact]
    public void VideoReceiptSurvivesVerifiedIdentityPromotionAndReferenceHandoff()
    {
        using var server=Database(); var source=server.AddSource(Path.GetTempPath()).Id;
        var videos=new Picer.Core.Video.VideoRepository(server);
        var original=new Picer.Core.Video.VideoRecord(10,source,Path.Combine(Path.GetTempPath(),"causal-video.mp4"),10,
            DateTime.UnixEpoch,null,0,0,null,null,"active");
        videos.UpsertMirrored(original,source);
        var request=new MetadataEdit("video-concept",10,"[\"animal\",\"manual\"]","accepted",
            DateTime.UnixEpoch.ToString("O"),Guid.NewGuid().ToString("N"));
        var first=Assert.Single(server.AcceptMetadataEdits([request]));
        Assert.StartsWith("local:",first.VideoContentIdentity);
        videos.UpsertMirrored(original with { Sha256=new string('a',64) },source);
        var verified=Assert.Single(server.AcceptMetadataEdits([request]));
        Assert.Equal(first.ServerVersion,verified.ServerVersion); Assert.StartsWith("sha256:",verified.VideoContentIdentity);
        videos.UpsertMirrored(original with { Id=20,Path=Path.Combine(Path.GetTempPath(),"causal-video-copy.mp4"),Sha256=new string('a',64) },source);
        using (var transaction=server.BeginTransaction())
        {
            server.ReassignVideoMetadataEdits(10);
            using var remove=server.Command("DELETE FROM videos WHERE id=10"); remove.Transaction=transaction; remove.ExecuteNonQuery(); transaction.Commit();
        }
        var after=Assert.Single(server.AcceptMetadataEdits([request with { EntityId=20 }]));
        Assert.Equal(20,after.EntityId); Assert.Equal(first.ServerVersion,after.ServerVersion);
        using (var transaction=server.BeginTransaction())
        {
            server.ReassignVideoMetadataEdits(20);
            using var remove=server.Command("DELETE FROM videos WHERE id=20"); remove.Transaction=transaction; remove.ExecuteNonQuery(); transaction.Commit();
        }
        using var receipts=server.Command("SELECT COUNT(*) FROM metadata_causal_receipts"); Assert.Equal(0L,receipts.ExecuteScalar());
    }
}
