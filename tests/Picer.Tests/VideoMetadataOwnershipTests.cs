using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Search;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class VideoMetadataOwnershipTests
{
    [Fact]
    public void VersionPromotionNeverRevivesAnOlderDecisionOnAnotherCopy()
    {
        using var database=new PicerDatabase(":memory:"); Add(database,10,'a'); Add(database,20,'a');
        var metadata=new VideoSearchMetadata(database); metadata.SetConcept(10,"animal","manual");
        var accepted=Assert.Single(database.AcceptMetadataEdits(database.GetMetadataEdits()));
        using (var seed=database.Command("""
            INSERT INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id,video_content_identity)
            VALUES('video-concept',20,$field,'rejected','2099-01-01T00:00:00.0000000Z',$op,$identity)
            """))
        {
            seed.Parameters.AddWithValue("$field",accepted.Field); seed.Parameters.AddWithValue("$op",Guid.NewGuid().ToString("N"));
            seed.Parameters.AddWithValue("$identity",accepted.VideoContentIdentity); seed.ExecuteNonQuery();
        }
        database.PromoteUnversionedMetadata();
        Assert.Equal(accepted,Assert.Single(database.GetMetadataEdits())); Assert.Contains("animal",metadata.GetConcepts(20));
        metadata.SetConcept(10,"animal","manual","rejected"); database.PromoteUnversionedMetadata();
        var newer=Assert.Single(database.GetMetadataEdits()); Assert.True(newer.ServerVersion>accepted.ServerVersion); Assert.Equal("rejected",newer.Value);
        database.PromoteUnversionedMetadata(); Assert.Equal(newer,Assert.Single(database.GetMetadataEdits()));
    }

    [Fact]
    public void SourceQuarantineRetainsEvidenceRemovesProjectionAndDoesNotEraseANewerEdit()
    {
        using var database = new PicerDatabase(":memory:"); Add(database, 10, 'a');
        var metadata = new VideoSearchMetadata(database);
        metadata.SetConcept(10, "animal", "manual");
        var old = Assert.Single(database.GetMetadataEdits());
        Add(database, 20, 'b');
        using (var transaction = database.BeginTransaction())
        {
            database.QuarantineVideoMetadataOperation(old.OperationId, transaction);
            transaction.Commit();
        }
        Assert.Empty(metadata.GetConcepts(10)); Assert.Empty(database.GetMetadataEdits());
        Assert.Empty(metadata.GetConcepts(20));
        Assert.Single(database.GetVideoOwnershipReviews());
        metadata.SetConcept(10, "animal", "manual", "rejected");
        var newer = Assert.Single(database.GetMetadataEdits());
        using (var transaction = database.BeginTransaction())
        {
            database.QuarantineVideoMetadataOperation(old.OperationId, transaction);
            transaction.Commit();
        }
        Assert.Equal(newer, Assert.Single(database.GetMetadataEdits()));
        Assert.Single(database.GetVideoOwnershipReviews());
    }

    [Fact]
    public void MigrationRetainsAmbiguousRawLegacyDecisionsForReview()
    {
        using var database = new PicerDatabase(":memory:");
        Add(database, 10, 'a'); Add(database, 20, 'b');
        var tag = SemanticEngine.AddTag(database, "旧标签").Id;
        using var command = database.Command("SELECT name FROM sqlite_master WHERE type='trigger' AND (name LIKE 'space_sync_video_legacy_purge_%' OR name='space_sync_video_verify_legacy' OR name LIKE 'space_sync_legacy_owner_%' OR name LIKE 'space_sync_owned_video_edit_%')");
        var triggers = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) triggers.Add(reader.GetString(0));
        foreach (var name in triggers)
        {
            command.CommandText = "DROP TRIGGER \"" + name.Replace("\"", "\"\"") + "\"";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            DROP VIEW video_effective_tags;
            DROP VIEW video_effective_concepts;
            DROP VIEW video_owned_metadata_edits;
            DROP TABLE video_metadata_ownership_reviews;
            DROP TABLE video_legacy_metadata_owners;
            DROP VIEW sync_video_owners;
            INSERT INTO video_tags(video_id,tag_id) SELECT content_id,$tag FROM videos WHERE id=10;
            INSERT INTO video_visual_concepts(video_id,concept_id,model_version,state)
                SELECT content_id,'风景','旧模型','accepted' FROM videos WHERE id=10;
            PRAGMA user_version=62;
            """;
        command.Parameters.AddWithValue("$tag", tag); command.ExecuteNonQuery();
        VideoMetadataOwnershipMigration.Apply(command.Connection!);
        var reviews = database.GetVideoOwnershipReviews();
        Assert.Equal(2, reviews.Count);
        Assert.All(reviews, review => { Assert.Equal(2, review.Candidates.Count); Assert.True(Guid.TryParseExact(review.OperationId, "N", out _)); });
        var metadata = new VideoSearchMetadata(database);
        Assert.Empty(metadata.GetTags(10)); Assert.Empty(metadata.GetTags(20));
        Assert.Empty(metadata.GetConcepts(10)); Assert.Empty(metadata.GetConcepts(20));
        var concept = Assert.Single(reviews.Where(review => review.Kind == "video-concept"));
        database.ResolveVideoOwnershipReview(new(concept.OperationId, [new(20, 0, null)], Epoch: concept.Epoch), authoritative: true);
        Assert.Contains("风景", metadata.GetConcepts(20)); Assert.Empty(metadata.GetConcepts(10));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("resolved")]
    [InlineData("discarded")]
    public void ArchivedOperationReplayCannotReviveOrOverwriteReviewedOwnership(string state)
    {
        using var database = new PicerDatabase(":memory:");
        Add(database, 10, 'a');
        Add(database, 20, 'b');
        var operation = Review(database);
        MetadataEdit archived;
        using (var query = database.Command("SELECT payload FROM video_metadata_ownership_reviews WHERE operation_id=$op"))
        {
            query.Parameters.AddWithValue("$op", operation);
            archived = System.Text.Json.JsonSerializer.Deserialize<MetadataEdit>((string)query.ExecuteScalar()!)!;
        }
        if (state != "pending")
        {
            var review = Assert.Single(database.GetVideoOwnershipReviews());
            database.ResolveVideoOwnershipReview(new(operation,
                state == "resolved" ? [new VideoOwnershipChoice(20, 0, null)] : [],
                Discard: state == "discarded", Epoch: review.Epoch), authoritative: true);
        }
        var before = database.GetMetadataEdits().ToArray();
        Assert.Empty(database.AcceptMetadataEdits([archived]));
        Assert.Empty(database.MergeMetadataEdits([archived]));
        // Old canonical-reference bindings must not gain authority by changing
        // the reference in a retried snapshot, either.
        Assert.Empty(database.AcceptMetadataEdits([archived with { EntityId = 20 }]));
        Assert.Equal(before, database.GetMetadataEdits().ToArray());
        var metadata = new VideoSearchMetadata(database);
        Assert.Empty(metadata.GetConcepts(10));
        if (state == "resolved") Assert.Single(metadata.GetConcepts(20));
        else Assert.Empty(metadata.GetConcepts(20));
    }

    private static string Review(PicerDatabase database)
    {
        var operation=Guid.NewGuid().ToString("N");
        var edit=new MetadataEdit("video-concept",10,"[\"animal\",\"manual\"]","accepted",DateTime.UtcNow.ToString("O"),operation)
            { VideoContentIdentity="sha256:"+new string('f',64) };
        using var insert=database.Command("INSERT INTO video_metadata_ownership_reviews(operation_id,content_id,kind,field,payload) SELECT $op,content_id,$kind,$field,$payload FROM videos WHERE id=10");
        insert.Parameters.AddWithValue("$op",operation); insert.Parameters.AddWithValue("$kind",edit.Kind);
        insert.Parameters.AddWithValue("$field",edit.Field); insert.Parameters.AddWithValue("$payload",System.Text.Json.JsonSerializer.Serialize(edit)); insert.ExecuteNonQuery();
        return operation;
    }
    private static void Add(PicerDatabase database,long id,char publisher,bool verified=true)
    {
        var root=PublishedMediaPath.Qualify(new string(publisher,32),@"C:\Videos");
        var source=database.AddSource(root).Id;
        new VideoRepository(database).UpsertMirrored(new VideoRecord(id,source,root+"\\"+id+".mp4",10,DateTime.UnixEpoch,
            null,0,0,null,null,"active") { Sha256=verified?new string('f',64):null },source);
    }

    [Fact]
    public void SameContentSharesDecisionsOnlyInsideItsOwningPublisher()
    {
        using var database=new PicerDatabase(":memory:"); Add(database,10,'a'); Add(database,11,'a'); Add(database,20,'b');
        var metadata=new VideoSearchMetadata(database); var tag=SemanticEngine.AddTag(database,"test").Id;
        metadata.SetTag(10,tag,true); metadata.SetConcept(10,"animal","manual");
        Assert.Contains(tag,metadata.GetTags(11)); Assert.Empty(metadata.GetTags(20));
        Assert.Contains("animal",metadata.GetConcepts(11)); Assert.Empty(metadata.GetConcepts(20));
        metadata.SetConcept(20,"animal","manual","rejected"); metadata.SetTag(20,tag,true); metadata.SetTag(10,tag,false);
        database.ReplayMetadataEdits();
        Assert.Empty(metadata.GetTags(10)); Assert.Contains(tag,metadata.GetTags(20));
        Assert.Contains("animal",metadata.GetConcepts(10)); Assert.Empty(metadata.GetConcepts(20));
        Assert.Equal(new long[]{20},metadata.Match([],[tag],[]).Order().ToArray());
        Assert.Equal(new long[]{10,11},metadata.Match([],[],["animal"]).Order().ToArray());
        Assert.Equal(4,database.GetMetadataEdits().Count);
    }

    [Fact]
    public void PurgingOneOwnersReferencesNeverTransfersItsEditsToAnotherOwner()
    {
        using var database=new PicerDatabase(":memory:"); Add(database,10,'a'); Add(database,11,'a'); Add(database,20,'b');
        var metadata=new VideoSearchMetadata(database); var tag=SemanticEngine.AddTag(database,"test").Id;
        metadata.SetTag(10,tag,false); metadata.SetTag(20,tag,true);
        foreach (var id in new long[]{10,11})
        {
            using var transaction=database.BeginTransaction(); database.ReassignVideoMetadataEdits(id);
            using var remove=database.Command("DELETE FROM videos WHERE id=$id"); remove.Transaction=transaction;
            remove.Parameters.AddWithValue("$id",id); remove.ExecuteNonQuery(); transaction.Commit();
        }
        database.ReplayMetadataEdits();
        var remaining=Assert.Single(database.GetMetadataEdits()); Assert.Equal(20,remaining.EntityId); Assert.Equal("1",remaining.Value);
        Assert.Contains(tag,metadata.GetTags(20));
        using var bindings=database.Command("SELECT COUNT(*) FROM video_metadata_operation_bindings WHERE reference_id<>20");
        Assert.Equal(0L,bindings.ExecuteScalar());
    }

    [Fact]
    public void LegacyDecisionKeepsItsKnownOwnerWhenAnotherPublisherAddsTheSameBytes()
    {
        using var database=new PicerDatabase(":memory:"); Add(database,10,'a');
        var tag=SemanticEngine.AddTag(database,"legacy").Id;
        using (var insert=database.Command("INSERT INTO video_tags(video_id,tag_id) SELECT content_id,$tag FROM videos WHERE id=10"))
        { insert.Parameters.AddWithValue("$tag",tag); insert.ExecuteNonQuery(); }
        Add(database,20,'b');
        var metadata=new VideoSearchMetadata(database);
        Assert.Contains(tag,metadata.GetTags(10)); Assert.Empty(metadata.GetTags(20));
        metadata.SetTag(20,tag,false);
        Assert.Contains(tag,metadata.GetTags(10)); Assert.Empty(metadata.GetTags(20));
    }

    [Fact]
    public void UnicodeConceptDecisionOverridesItsLegacyProjectionAfterASecondOwnerArrives()
    {
        using var database=new PicerDatabase(":memory:"); Add(database,10,'a');
        var metadata=new VideoSearchMetadata(database); metadata.SetConcept(10,"风景","模型一");
        Add(database,20,'b'); metadata.SetConcept(10,"风景","模型一","rejected");
        Assert.Empty(metadata.GetConcepts(10)); Assert.Empty(metadata.GetConcepts(20));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoricalReviewAppliesOnlyToSelectedOwnerAndRetryIsIdempotent(bool authoritative)
    {
        using var database=new PicerDatabase(":memory:"); Add(database,10,'a'); Add(database,20,'b');
        var operation=Review(database); var review=Assert.Single(database.GetVideoOwnershipReviews());
        Assert.Equal(2,review.Candidates.Count);
        var first=review.Candidates.Single(candidate=>candidate.VideoId==10);
        var request=new VideoOwnershipResolution(operation,[new(first.VideoId,first.Version,first.CurrentOperationId)],Epoch:review.Epoch);
        database.ResolveVideoOwnershipReview(request,authoritative);
        var accepted=Assert.Single(database.GetMetadataEdits());
        Assert.Equal(authoritative,accepted.ServerVersion>0);
        var metadata=new VideoSearchMetadata(database);
        Assert.Contains("animal",metadata.GetConcepts(10)); Assert.Empty(metadata.GetConcepts(20));
        Assert.Empty(database.GetVideoOwnershipReviews());
        database.ResolveVideoOwnershipReview(request,authoritative); Assert.Equal(accepted,Assert.Single(database.GetMetadataEdits()));
        Assert.Throws<InvalidOperationException>(()=>database.ResolveVideoOwnershipReview(new(operation,[],Discard:true,Epoch:review.Epoch),authoritative));
        Assert.Empty(metadata.GetConcepts(20));
    }

    [Fact]
    public void HistoricalReviewRejectsStaleDecisionsAndRollsBackInvalidOwnerSelections()
    {
        using var database=new PicerDatabase(":memory:"); Add(database,10,'a'); Add(database,20,'b'); Add(database,30,'c');
        var repository=new VideoRepository(database); var unrelated=repository.GetAll().Single(video=>video.Id==30);
        repository.UpsertMirrored(unrelated with { Sha256=new string('e',64) },unrelated.SourceId);
        var operation=Review(database);
        var epoch=Assert.Single(database.GetVideoOwnershipReviews()).Epoch;
        Assert.Throws<ArgumentException>(()=>database.ResolveVideoOwnershipReview(new(operation,[new(10,0,null),new(30,0,null)],Epoch:epoch),authoritative:true));
        Assert.Empty(database.GetMetadataEdits()); Assert.Single(database.GetVideoOwnershipReviews());
        var metadata=new VideoSearchMetadata(database); metadata.SetConcept(10,"animal","manual","rejected");
        Assert.Throws<InvalidOperationException>(()=>database.ResolveVideoOwnershipReview(new(operation,[new(10,0,null)])));
        Assert.Single(database.GetVideoOwnershipReviews());
        database.ResolveVideoOwnershipReview(new(operation,[],Discard:true));
        Assert.Empty(database.GetVideoOwnershipReviews()); Assert.Empty(metadata.GetConcepts(10));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VerifyingLegacyContentKeepsItsDecisionsOrPreservesAmbiguityWithoutLeakingToAnExistingOwner(bool knownOwner)
    {
        using var database=new PicerDatabase(":memory:"); Add(database,10,'a',false);
        var tag=SemanticEngine.AddTag(database,"legacy").Id;
        using (var insert=database.Command("""
            INSERT INTO video_tags(video_id,tag_id) SELECT content_id,$tag FROM videos WHERE id=10;
            INSERT INTO video_visual_concepts(video_id,concept_id,model_version,state) SELECT content_id,'风景','旧模型','accepted' FROM videos WHERE id=10;
            """))
        { insert.Parameters.AddWithValue("$tag",tag); insert.ExecuteNonQuery(); }
        if (!knownOwner)
        { using var forget=database.Command("DELETE FROM video_legacy_metadata_owners"); forget.ExecuteNonQuery(); }
        Add(database,20,'b');
        var repository=new VideoRepository(database); var original=repository.GetAll().Single(video=>video.Id==10);
        repository.UpsertMirrored(original with { Sha256=new string('f',64) },original.SourceId);
        var metadata=new VideoSearchMetadata(database);
        Assert.Empty(metadata.GetTags(20)); Assert.Empty(metadata.GetConcepts(20));
        if (knownOwner)
        {
            Assert.Contains(tag,metadata.GetTags(10)); Assert.Contains("风景",metadata.GetConcepts(10));
            metadata.SetConcept(10,"风景","旧模型","rejected"); Assert.Empty(metadata.GetConcepts(10));
            Assert.All(database.GetMetadataEdits(),edit=>Assert.Equal(10,edit.EntityId));
        }
        else
        {
            Assert.Empty(metadata.GetTags(10)); Assert.Empty(metadata.GetConcepts(10));
            using var retained=database.Command("SELECT COUNT(*) FROM video_metadata_ownership_reviews WHERE state='pending'");
            Assert.Equal(2L,retained.ExecuteScalar());
            using var payloads=database.Command("SELECT payload FROM video_metadata_ownership_reviews");
            using var rows=payloads.ExecuteReader();
            while (rows.Read())
            {
                var edit=System.Text.Json.JsonSerializer.Deserialize<MetadataEdit>(rows.GetString(0));
                Assert.NotNull(edit); Assert.True(Guid.TryParseExact(edit.OperationId,"N",out _)); Assert.False(string.IsNullOrEmpty(edit.Field));
            }
        }
    }
}
