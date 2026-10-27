using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Video;
using Picer.Core.Gateway;
using Picer.Core.People;

namespace Picer.Tests;

public sealed class CatalogRetirementApplicationTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"picer-catalog-application-"+Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void RelationshipAddedDuringCompletionHasAnActionableReviewWithoutChangingOriginalReceiptCount(bool keep,bool additionalMedia)
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var (catalog,scope,anchor)=Seed(database,false,true);
        Assert.Equal("applied",Apply(database,anchor,scope).State);
        var media=additionalMedia?30L:10L;
        if(additionalMedia)
        {
            database.UpsertMirroredImage(new ImageRecord { Id=media,SourceId=scope.SourceIds.Single(),Path=@"C:\Photos\new.jpg",FileSize=10,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
            using var identity=database.Command("INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id) VALUES('publisher','image','30',$global,30)");
            identity.Parameters.AddWithValue("$global",Guid.NewGuid().ToString("N"));identity.ExecuteNonQuery();
        }
        SemanticEngine.AssignTag(database,media,catalog);
        CatalogRetirementCompletion Complete()
        {
            using var transaction=database.BeginTransaction();
            var result=database.FinalizeCatalogRetirement("publisher",anchor.OperationId,anchor.Kind,anchor.GlobalId,1,scope,transaction);
            transaction.Commit();return result;
        }
        Assert.Equal("conflict",Complete().State);
        var review=Assert.Single(database.GetCatalogRetirementReviews());Assert.True(review.CompletionOnly);Assert.Equal(media,review.MediaId);
        SemanticEngine.RemoveTag(database,media,catalog);SemanticEngine.AssignTag(database,media,catalog);
        Assert.Throws<InvalidDataException>(()=>database.ResolveCatalogRetirementReview(new(review,keep),scope));
        review=Assert.Single(database.GetCatalogRetirementReviews());
        database.ResolveCatalogRetirementReview(new(review,keep),scope);
        Assert.Equal("completed",Complete().State);Assert.Equal(keep,HasLink(database,false,catalog,media));
        Assert.True(HasLink(database,false,catalog,20));Assert.Empty(database.GetCatalogRetirementReviews());
        Assert.Equal(0,database.CountPendingCatalogSynchronization());
        using var receipts=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_receipts");Assert.Equal(1L,receipts.ExecuteScalar());
    }

    [Fact]
    public void CompactionRequiresCompletionRetainsCausalProofAndCannotRecaptureRetiredIdentity()
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var (catalog,scope,anchor)=Seed(database,false,true);
        var manifest=new CatalogRetirementManifest(anchor.OperationId,anchor.Kind,anchor.GlobalId,1);
        CatalogRetirementCompletion Compact()
        {
            using var transaction=database.BeginTransaction();
            var result=database.CompactCatalogRetirement("publisher",manifest,scope,transaction);transaction.Commit();return result;
        }
        Assert.Equal("pending",Compact().State);
        Assert.Equal("applied",Apply(database,anchor,scope).State);
        Assert.Equal("pending",Compact().State);
        using(var transaction=database.BeginTransaction())
        {
            Assert.Equal("completed",database.FinalizeCatalogRetirement("publisher",anchor.OperationId,anchor.Kind,anchor.GlobalId,1,scope,transaction).State);
            transaction.Commit();
        }
        Assert.Single(database.GetCatalogCompactionCandidates("publisher",scope));
        Assert.Equal("completed",Compact().State);Assert.Equal("completed",Compact().State);
        Assert.Empty(database.GetCatalogCompactionCandidates("publisher",scope));
        using(var receipts=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_receipts")) Assert.Equal(0L,receipts.ExecuteScalar());
        using(var proof=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_predecessors")) Assert.Equal(1L,proof.ExecuteScalar());
        using(var transaction=database.BeginTransaction())
        { Assert.Empty(database.RecordCatalogRetirement("tag",catalog,transaction));transaction.Commit(); }
        SemanticEngine.AssignTag(database,10,catalog);
        using var obsolete=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_predecessors");Assert.Equal(0L,obsolete.ExecuteScalar());
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void ReviewedCatalogConflictRetiresOldIdentityAndPreservesOnlyExplicitlyKeptAssociations(bool keep,bool receiving)
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var (catalog,scope,original)=Seed(database,false,receiving);
        var anchor=original with { Fingerprint=null };
        Assert.Equal("baseline_required",Apply(database,anchor,scope).State);
        var review=Assert.Single(database.GetCatalogRetirementReviews());
        Assert.Throws<InvalidDataException>(()=>database.ResolveCatalogRetirementReview(new(review with { Epoch="old" },keep),scope));
        Assert.Throws<UnauthorizedAccessException>(()=>database.ResolveCatalogRetirementReview(new(review,keep),scope with { SourceIds=[] }));
        SemanticEngine.RemoveTag(database,10,catalog);SemanticEngine.AssignTag(database,10,catalog);
        Assert.Throws<InvalidDataException>(()=>database.ResolveCatalogRetirementReview(new(review,keep),scope));
        review=Assert.Single(database.GetCatalogRetirementReviews());
        database.ResolveCatalogRetirementReview(new(review,keep),scope);
        Assert.Empty(database.GetCatalogRetirementReviews());
        Assert.Equal(keep?"kept":"applied",Apply(database,anchor,scope).State);
        Assert.Equal(keep,HasLink(database,false,catalog,10));
        using var transaction=database.BeginTransaction();
        var result=database.FinalizeCatalogRetirement("publisher",anchor.OperationId,"tag",anchor.GlobalId,1,scope,transaction);
        Assert.Equal("completed",result.State);Assert.False(result.CatalogRemoved);transaction.Commit();
        Assert.True(HasLink(database,false,catalog,20));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitMediaPurgeDoesNotLeaveCatalogCompletionWaitingForForgottenReceipts(bool applied)
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var (catalog,scope,anchor)=Seed(database,false,true);
        if(applied) Assert.Equal("applied",Apply(database,anchor,scope).State);
        else
        {
            using var transaction=database.BeginTransaction();
            var operations=database.RecordCatalogRetirement("tag",catalog,transaction);
            database.SealCatalogRetirements(operations,transaction);transaction.Commit();
            anchor=Assert.Single(database.GetPendingCatalogRetirementAnchors("publisher",scope));
        }
        Assert.True(new ImageRecordPurger(database).Purge(10,requireMissing:false));
        Assert.Null(database.GetImage(10));
        using(var query=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_receipts")) Assert.Equal(0L,query.ExecuteScalar());
        using(var query=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_media")) Assert.Equal(0L,query.ExecuteScalar());
        using(var query=database.Command("SELECT purged_count FROM sync_catalog_purged_receipt_counts")) Assert.Equal(1L,query.ExecuteScalar());
        using(var transaction=database.BeginTransaction())
        {
            var result=database.FinalizeCatalogRetirement("publisher",anchor.OperationId,"tag",anchor.GlobalId,1,scope,transaction);
            Assert.Equal("completed",result.State);Assert.False(result.CatalogRemoved);transaction.Commit();
        }
        Assert.True(HasLink(database,false,catalog,20));
        using var cleared=database.Command("SELECT COUNT(*) FROM sync_catalog_purged_receipt_counts");Assert.Equal(0L,cleared.ExecuteScalar());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedRetirementIsNotALocalUserEditButNewUserOperationsStillConflict(bool samePublisher)
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var (catalog,scope,anchor)=Seed(database,false,false);
        Assert.Equal("applied",Apply(database,anchor,scope).State);
        var old=database.GetMetadataEdits().Single(edit=>edit.Kind=="tag-link" && edit.EntityId==10);
        var remote=old with { Value="1",OperationId=Guid.NewGuid().ToString("N"),ServerVersion=100,BaseVersion=0 };
        using(var transaction=database.BeginTransaction())
        {
            database.MergeMetadataEditsInTransaction([remote],transaction,importedPublisher:samePublisher?"publisher":"other");
            transaction.Commit();
        }
        Assert.Equal(samePublisher?0:1,database.GetMetadataEditConflicts().Count);
        if(!samePublisher) return;
        SemanticEngine.RemoveTag(database,10,catalog);
        var user=database.GetMetadataEdits().Single(edit=>edit.Kind=="tag-link" && edit.EntityId==10);
        using(var transaction=database.BeginTransaction())
        {
            database.MergeMetadataEditsInTransaction([remote with { OperationId=Guid.NewGuid().ToString("N"),ServerVersion=101 }],transaction,importedPublisher:"publisher");
            transaction.Commit();
        }
        Assert.Equal(user.OperationId,Assert.Single(database.GetMetadataEditConflicts()).Proposed.OperationId);
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void CompletionWaitsForReceiptsAndPreservesPrivateReferences(bool album,bool receiving)
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var (catalog,scope,anchor)=Seed(database,album,receiving);
        CatalogRetirementCompletion Complete(long count=1,SpaceUploadScope? selected=null)
        {
            using var transaction=database.BeginTransaction();
            var result=database.FinalizeCatalogRetirement("publisher",anchor.OperationId,anchor.Kind,anchor.GlobalId,count,selected??scope,transaction);
            transaction.Commit();return result;
        }
        Assert.Equal("pending",Complete().State);
        Assert.Equal("applied",Apply(database,anchor,scope).State);
        using(var evidence=database.Command("SELECT applied_operations FROM sync_catalog_retirement_receipts"))
            Assert.NotEmpty(System.Text.Json.JsonSerializer.Deserialize<string[]>((string)evidence.ExecuteScalar()!)!);
        if(album) Assert.Throws<UnauthorizedAccessException>(()=>Complete(selected:scope with { Photos=false,Videos=true }));
        else Assert.Equal("scope_conflict",Complete(selected:scope with { Photos=false,Videos=true }).State);
        var result=Complete();Assert.Equal("completed",result.State);Assert.False(result.CatalogRemoved);
        Assert.Equal(result,Complete());
        Assert.Throws<InvalidDataException>(()=>Complete(2));
        Assert.True(database.IsRetiredCatalogIdentity("publisher",anchor.Kind,anchor.GlobalId));
        Assert.True(HasLink(database,album,catalog,20));
        using var active=database.Command("SELECT COUNT(*) FROM sync_active_entity_identities WHERE kind=$kind");
        active.Parameters.AddWithValue("$kind",anchor.Kind);Assert.Equal(0L,active.ExecuteScalar());
        // A shared private catalogue can acquire a new identity before the peer
        // retries a lost completion response. The old receipt remains valid.
        using(var rotate=database.Command("UPDATE sync_entity_identities SET global_id=$new WHERE publisher_id='publisher' AND kind=$kind"))
        {
            rotate.Parameters.AddWithValue("$new",Guid.NewGuid().ToString("N"));
            rotate.Parameters.AddWithValue("$kind",anchor.Kind);rotate.ExecuteNonQuery();
        }
        Assert.Equal(result,Complete());
        Assert.Throws<InvalidDataException>(()=>Complete(2));
        Assert.Throws<UnauthorizedAccessException>(()=>Complete(selected:scope with { SourceIds=Array.Empty<long>() }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionRejectsNewRelationshipsAndRollsBackItsFenceOnDeleteFailure(bool album)
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var (catalog,scope,anchor)=Seed(database,album,true);
        CatalogRetirementCompletion Complete()
        {
            using var transaction=database.BeginTransaction();
            var result=database.FinalizeCatalogRetirement("publisher",anchor.OperationId,anchor.Kind,anchor.GlobalId,1,scope,transaction);
            transaction.Commit();return result;
        }
        Assert.Equal("applied",Apply(database,anchor,scope).State);
        if(album) SemanticEngine.AddToAlbum(database,catalog,10);else SemanticEngine.AssignTag(database,10,catalog);
        Assert.Equal("conflict",Complete().State);
        Assert.False(database.IsRetiredCatalogIdentity("publisher",anchor.Kind,anchor.GlobalId));
        foreach(var id in new[]{10L,20L})
            if(album) SemanticEngine.RemoveFromAlbum(database,catalog,id);else SemanticEngine.RemoveTag(database,id,catalog);
        using(var fail=database.Command($"CREATE TRIGGER test_finalization_failure BEFORE DELETE ON {(album?"albums":"tags")} BEGIN SELECT RAISE(ABORT,'test_finalize'); END")) fail.ExecuteNonQuery();
        Assert.Throws<SqliteException>(()=>Complete());
        Assert.False(database.IsRetiredCatalogIdentity("publisher",anchor.Kind,anchor.GlobalId));
        using(var allow=database.Command("DROP TRIGGER test_finalization_failure")) allow.ExecuteNonQuery();
        Assert.True(Complete().CatalogRemoved);
        Assert.True(database.IsRetiredCatalogIdentity("publisher",anchor.Kind,anchor.GlobalId));
        using var count=database.Command($"SELECT COUNT(*) FROM {(album?"albums":"tags")} WHERE id=$id");
        count.Parameters.AddWithValue("$id",catalog);Assert.Equal(0L,count.ExecuteScalar());
    }

    [Theory]
    [InlineData(false,false,false)]
    [InlineData(false,true,false)]
    [InlineData(false,true,true)]
    [InlineData(true,false,false)]
    [InlineData(true,true,false)]
    [InlineData(true,true,true)]
    public void VideoContentRetirementRespectsOtherOwnersAndExcludedCopies(bool person,bool sameOwner,bool fullScope)
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var (tag,initialScope,original)=Seed(database,false,true);
        var first=initialScope.SourceIds.Single();var second=database.GetSources().Single(s=>s.Id!=first).Id;
        var catalog=person?123:tag;var kind=person?"person":"tag";
        if(person) database.UpsertMirroredPerson(new(123,"测试人物",0,0));
        var global=person?Guid.NewGuid().ToString("N"):original.GlobalId;
        var media=Guid.NewGuid().ToString("N");
        using(var seed=database.Command("""
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
            VALUES('other','other-library','other-device','other-grant','space','receiving','epoch');
            INSERT INTO sync_source_bindings(publisher_id,local_source_id,source_id,source_uuid,original_path)
            VALUES($owner,$source,$source,'second-source','D:\Private');
            INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id)
            VALUES('publisher','video','30',$media,30),($owner,'video','40',$secondMedia,40);
            """))
        {
            seed.Parameters.AddWithValue("$owner",sameOwner?"publisher":"other");seed.Parameters.AddWithValue("$source",second);
            seed.Parameters.AddWithValue("$media",media);seed.Parameters.AddWithValue("$secondMedia",Guid.NewGuid().ToString("N"));seed.ExecuteNonQuery();
        }
        if(person)
        {
            using var identity=database.Command("INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id) VALUES('publisher','person','123',$global,123)");
            identity.Parameters.AddWithValue("$global",global);identity.ExecuteNonQuery();
        }
        var videos=new VideoRepository(database);
        videos.UpsertMirrored(new(30,first,@"C:\Photos\one.mp4",10,DateTime.UnixEpoch,30,100,100,null,null,"active") { Sha256=new string('a',64) },first);
        videos.UpsertMirrored(new(40,second,@"D:\Private\two.mp4",10,DateTime.UnixEpoch,30,100,100,null,null,"active") { Sha256=new string('a',64) },second);
        if(person)
        {
            var now=DateTime.UtcNow.ToString("O");
            var faces=new List<GatewayVideoFace> { new(101,30,100,0,0,0,0,10,10,0.9,null,null,null,catalog,"confirmed",now,now) { ModelVersion="test-model" } };
            if(!sameOwner) faces.Add(new(102,40,100,0,0,0,0,10,10,0.9,null,null,null,catalog,"confirmed",now,now) { ModelVersion="test-model" });
            database.UpsertMirroredVideoMetadata(new GatewayMetadata([],[],[]) with { VideoKeyframes=[new(100,30,0,"",123)],VideoFaces=faces });
            using var transaction=database.BeginTransaction();
            foreach(var face in faces) database.RecordVideoFaceManualState(face.Id,transaction);
            transaction.Commit();
        }
        else
        {
            new VideoSearchMetadata(database).SetTag(30,catalog,true);
            if(!sameOwner) new VideoSearchMetadata(database).SetTag(40,catalog,true);
        }
        var scope=new SpaceUploadScope(fullScope?[first,second]:[first],People:person);
        var anchor=new CatalogRetirementAnchor(Guid.NewGuid().ToString("N"),kind,global,"video",media,first)
            { Fingerprint=database.GetCatalogRetirementFingerprint(kind,catalog,"video",30) };
        var blocked=sameOwner&&!fullScope;
        Assert.Equal(blocked?"scope_conflict":"applied",Apply(database,anchor,scope).State);
        bool Has(long id)
        {
            using var query=database.Command(person?"SELECT 1 FROM video_effective_faces WHERE reference_id=$id AND assigned_person_id=$catalog"
                :"SELECT 1 FROM video_effective_tags WHERE video_id=$id AND tag_id=$catalog");
            query.Parameters.AddWithValue("$id",id);query.Parameters.AddWithValue("$catalog",catalog);return query.ExecuteScalar() is not null;
        }
        Assert.Equal(blocked,Has(30));Assert.Equal(!sameOwner||blocked,Has(40));
        Assert.Equal(2,videos.GetAll().Count);
        if(person) Assert.Throws<UnauthorizedAccessException>(()=>Apply(database,anchor,scope with { People=false }));
        if(blocked)
        {
            Assert.Equal("scope_conflict",Apply(database,anchor,scope).State);
            Assert.Equal("applied",Apply(database,anchor,scope with { SourceIds=[first,second] }).State);
            Assert.False(Has(30));Assert.False(Has(40));
        }
    }

    [Fact]
    public void PhotoPersonRetirementPreservesPrivateAssignments()
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var (_,scope,_)=Seed(database,false,true);
        database.UpsertMirroredPerson(new(123,"测试人物",0,0));
        foreach(var (face,image) in new[]{(51L,10L),(52L,20L)})
        {
            database.UpsertMirroredFace(new(face,image,"test-model",0,0,0,10,10,0.9,null,null,DateTime.UtcNow.ToString("O")));
            new PeopleService(database).ConfirmFace(face,123);
        }
        var global=Guid.NewGuid().ToString("N");
        using(var identity=database.Command("INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id) VALUES('publisher','person','123',$global,123)"))
        { identity.Parameters.AddWithValue("$global",global);identity.ExecuteNonQuery(); }
        using var media=database.Command("SELECT global_id FROM sync_entity_identities WHERE publisher_id='publisher' AND kind='image'");
        var anchor=new CatalogRetirementAnchor(Guid.NewGuid().ToString("N"),"person",global,"image",(string)media.ExecuteScalar()!,scope.SourceIds.Single())
            { Fingerprint=database.GetCatalogRetirementFingerprint("person",123,"image",10) };
        Assert.Equal("applied",Apply(database,anchor,scope with { People=true }).State);
        Assert.Equal(52,Assert.Single(database.GetAllMirroredFaceAssignments()).FaceId);
        Assert.NotNull(new PeopleService(database).Get(123));
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void ApplyIsScopedIdempotentAndRechecksAuthorization(bool album,bool receiving)
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var (catalog,scope,anchor)=Seed(database,album,receiving);
        var first=Apply(database,anchor,scope);
        Assert.Equal("applied",first.State);Assert.Equal(first,Apply(database,anchor,scope));
        Assert.False(HasLink(database,album,catalog,10));Assert.True(HasLink(database,album,catalog,20));
        Assert.NotNull(database.GetImage(10));Assert.NotNull(database.GetImage(20));
        Assert.Throws<UnauthorizedAccessException>(()=>Apply(database,anchor,scope with { Photos=false }));
        Assert.Throws<InvalidDataException>(()=>Apply(database,anchor with { Fingerprint=null },scope));
        using(var count=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_receipts")) Assert.Equal(1L,count.ExecuteScalar());
        var edit=database.GetMetadataEdits().Single(e=>e.Kind==(album?"album-link":"tag-link")
            && (album?e.Field=="10":e.EntityId==10));
        Assert.Equal("0",edit.Value);Assert.Equal(receiving,edit.ServerVersion>0);
        if(!receiving)
        {
            using var imported=database.Command("SELECT COUNT(*) FROM sync_imported_metadata_edits WHERE operation_id=$operation");
            imported.Parameters.AddWithValue("$operation",edit.OperationId);Assert.Equal(1L,imported.ExecuteScalar());
        }
        Assert.True(new ImageRecordPurger(database).Purge(10,requireMissing:false));
        using var purged=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_receipts");Assert.Equal(0L,purged.ExecuteScalar());
        Assert.True(HasLink(database,album,catalog,20));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingBaselineAndConcurrentEditPreserveTheRelationship(bool missing)
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var (catalog,scope,anchor)=Seed(database,false,true);
        if(missing) anchor=anchor with { Fingerprint=null };
        else { SemanticEngine.RemoveTag(database,10,catalog);SemanticEngine.AssignTag(database,10,catalog); }
        var result=Apply(database,anchor,scope);
        Assert.Equal(missing?"baseline_required":"conflict",result.State);
        Assert.True(HasLink(database,false,catalog,10));
        Assert.Equal(result,Apply(database,anchor,scope));
    }

    [Fact]
    public void LegacyFingerprintDoesNotAuthorizeAnAutomaticDeletion()
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var (catalog,scope,anchor)=Seed(database,false,true);
        Assert.Equal("baseline_required",Apply(database,anchor with { Fingerprint="v1:"+anchor.Fingerprint![3..] },scope).State);
        Assert.True(HasLink(database,false,catalog,10));
    }

    [Fact]
    public void ReceiptFailureRollsBackTheRelationshipAndItsField()
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var (catalog,scope,anchor)=Seed(database,false,true);
        var before=database.GetCatalogRetirementFingerprint("tag",catalog,"image",10);
        using(var fail=database.Command("CREATE TRIGGER test_receipt_failure BEFORE INSERT ON sync_catalog_retirement_receipts BEGIN SELECT RAISE(ABORT,'test_receipt_failure'); END")) fail.ExecuteNonQuery();
        Assert.Throws<SqliteException>(()=>Apply(database,anchor,scope));
        Assert.True(HasLink(database,false,catalog,10));
        Assert.Equal(before,database.GetCatalogRetirementFingerprint("tag",catalog,"image",10));
        using var count=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_receipts");Assert.Equal(0L,count.ExecuteScalar());
    }

    private static CatalogRetirementApplyResult Apply(PicerDatabase database,CatalogRetirementAnchor anchor,SpaceUploadScope scope)
    {
        using var transaction=database.BeginTransaction();
        var result=database.ApplyCatalogRetirementAssociation("publisher",anchor,scope,transaction);
        transaction.Commit();return result;
    }

    private static bool HasLink(PicerDatabase database,bool album,long catalog,long image)
        =>album?SemanticEngine.GetAlbumImages(database,catalog).Any(i=>i.Id==image)
            :SemanticEngine.GetImageTags(database,image).Any(t=>t.Id==catalog);

    private static (long Catalog,SpaceUploadScope Scope,CatalogRetirementAnchor Anchor) Seed(PicerDatabase database,bool album,bool receiving)
    {
        var first=database.AddSource(@"C:\Photos");var second=database.AddSource(@"D:\Private");
        foreach(var (id,source,path) in new[]{(10L,first.Id,@"C:\Photos\one.jpg"),(20L,second.Id,@"D:\Private\two.jpg")})
            database.UpsertMirroredImage(new ImageRecord { Id=id,SourceId=source,Path=path,FileSize=10,
                ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
        var kind=album?"album":"tag";
        var catalog=album?SemanticEngine.AddAlbum(database,"测试相册").Id:SemanticEngine.AddTag(database,"测试标签").Id;
        foreach(var id in new[]{10L,20L})
            if(album) SemanticEngine.AddToAlbum(database,catalog,id);else SemanticEngine.AssignTag(database,id,catalog);
        var global=Guid.NewGuid().ToString("N");var media=Guid.NewGuid().ToString("N");
        using(var seed=database.Command("""
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
            VALUES('publisher','library','device','grant','space',$state,'epoch');
            INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id)
            VALUES('publisher',$kind,$key,$global,$catalog),('publisher','image','10',$media,10);
            INSERT INTO sync_source_bindings(publisher_id,local_source_id,source_id,source_uuid,original_path)
            VALUES('publisher',$source,$source,'source','C:\Photos');
            """))
        {
            seed.Parameters.AddWithValue("$state",receiving?"receiving":"publishing");seed.Parameters.AddWithValue("$kind",kind);
            seed.Parameters.AddWithValue("$key",catalog.ToString(System.Globalization.CultureInfo.InvariantCulture));
            seed.Parameters.AddWithValue("$catalog",catalog);seed.Parameters.AddWithValue("$global",global);
            seed.Parameters.AddWithValue("$media",media);seed.Parameters.AddWithValue("$source",first.Id);seed.ExecuteNonQuery();
        }
        return (catalog,new([first.Id]),new(Guid.NewGuid().ToString("N"),kind,global,"image",media,first.Id)
            { Fingerprint=database.GetCatalogRetirementFingerprint(kind,catalog,"image",10,publisher:"publisher") });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();if(Directory.Exists(_root)) Directory.Delete(_root,true);
    }
}
