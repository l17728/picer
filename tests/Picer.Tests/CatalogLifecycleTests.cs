using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Identity;

namespace Picer.Tests;

public sealed class CatalogLifecycleTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"picer-catalog-lifecycle-"+Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void RetirementCapturesOnlyPublishedAnchorsAndRollsBackWithDeletion(bool album,bool receiving)
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var first=database.AddSource(@"C:\Photos");var second=database.AddSource(@"D:\Private");
        foreach(var (id,source,path) in new[]{(10L,first.Id,@"C:\Photos\one.jpg"),(20L,second.Id,@"D:\Private\two.jpg")})
            database.UpsertMirroredImage(new ImageRecord { Id=id,SourceId=source,Path=path,FileSize=10,
                ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
        var kind=album?"album":"tag";
        var catalog=album?SemanticEngine.AddAlbum(database,"本地目录名称").Id:SemanticEngine.AddTag(database,"本地目录名称").Id;
        foreach(var id in new[]{10L,20L})
            if(album) SemanticEngine.AddToAlbum(database,catalog,id);else SemanticEngine.AssignTag(database,id,catalog);
        using(var seed=database.Command("""
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
            VALUES('a','library-a','device-a','grant-a','space',$state,'epoch'),('b','library-b','device-b','grant-b','space',$state,'epoch');
            INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id)
            VALUES('a',$kind,$local,'catalog-a',$catalog),('a','image','10','image-a',10),
                ('b',$kind,$other,'catalog-b',$catalog),('b','image','20','image-b',20);
            INSERT INTO sync_source_bindings(publisher_id,local_source_id,source_id,source_uuid,original_path)
            VALUES('a',$first,$first,'source-a','C:\Photos'),('b',$second,$second,'source-b','D:\Private');
            """))
        {
            seed.Parameters.AddWithValue("$state",receiving?"receiving":"publishing");seed.Parameters.AddWithValue("$kind",kind);
            seed.Parameters.AddWithValue("$catalog",catalog);seed.Parameters.AddWithValue("$local",catalog.ToString(System.Globalization.CultureInfo.InvariantCulture));
            seed.Parameters.AddWithValue("$first",first.Id);seed.Parameters.AddWithValue("$second",second.Id);
            seed.Parameters.AddWithValue("$other",receiving?catalog.ToString(System.Globalization.CultureInfo.InvariantCulture):"999");seed.ExecuteNonQuery();
        }
        if(!album)
        {
            foreach(var (id,source,path) in new[]{(30L,first.Id,@"C:\Photos\one.mp4"),(40L,second.Id,@"D:\Private\two.mp4")})
            {
                new Picer.Core.Video.VideoRepository(database).UpsertMirrored(new(id,source,path,10,DateTime.UnixEpoch,null,0,0,null,null,"active")
                    { Sha256=new string('a',64) },source);
                new Picer.Core.Video.VideoSearchMetadata(database).SetTag(id,catalog,true);
            }
            using var videos=database.Command("INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id) VALUES('a','video','30','video-a',30),('b','video','40','video-b',40)");
            videos.ExecuteNonQuery();
        }
        var expectedFingerprint=database.GetCatalogRetirementFingerprint(kind,catalog,"image",10,publisher:"a");
        using(var fail=database.Command($"CREATE TRIGGER test_retirement_abort BEFORE DELETE ON {(album?"albums":"tags")} BEGIN SELECT RAISE(ABORT,'retirement_test_failed'); END")) fail.ExecuteNonQuery();
        void Delete() { if(album) SemanticEngine.DeleteAlbum(database,catalog);else SemanticEngine.DeleteTag(database,catalog); }
        Assert.Throws<SqliteException>(Delete);
        using(var count=database.Command("SELECT COUNT(*) FROM sync_catalog_retirements")) Assert.Equal(0L,count.ExecuteScalar());
        using(var count=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_baselines")) Assert.Equal(0L,count.ExecuteScalar());
        using(var allow=database.Command("DROP TRIGGER test_retirement_abort")) allow.ExecuteNonQuery();
        Delete();
        using(var count=database.Command("SELECT COUNT(*) FROM sync_catalog_retirements")) Assert.Equal(receiving?2L:1L,count.ExecuteScalar());
        using(var count=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_media")) Assert.Equal((receiving?2L:1L)*(album?1:2),count.ExecuteScalar());
        using(var foreign=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_media m JOIN sync_catalog_retirements r ON r.operation_id=m.operation_id WHERE r.publisher_id='a' AND m.media_id NOT IN (10,30)"))
            Assert.Equal(0L,foreign.ExecuteScalar());
        var scope=new SpaceUploadScope(new[]{first.Id});
        Assert.Equal(receiving?(album?1:2):0,
            database.GetPendingCatalogRetirementAnchors("a",scope,requirePublishedCut:true).Count);
        using(var captured=database.Command("SELECT captured_media_count FROM sync_catalog_retirements WHERE publisher_id='a'"))
            Assert.Equal(album?1L:2L,captured.ExecuteScalar());
        if(!receiving)
        {
            using(var acknowledge=database.Command("UPDATE sync_publishers SET uploaded_metadata_revision=(SELECT sealed_metadata_revision FROM sync_catalog_retirements WHERE publisher_id='a') WHERE publisher_id='a'"))
                acknowledge.ExecuteNonQuery();
            // Later unrelated edits must not postpone this already published deletion.
            SemanticEngine.AddTag(database,"后续独立编辑");
            Assert.Equal(album?1:2,database.GetPendingCatalogRetirementAnchors("a",scope,requirePublishedCut:true).Count);
        }
        Assert.Empty(database.GetPendingCatalogRetirementAnchors("a",new(new[]{second.Id})));
        Assert.Empty(database.GetPendingCatalogRetirementAnchors("missing",scope));
        Assert.Empty(database.GetPendingCatalogRetirementAnchors("a",scope with { Photos=false,Videos=false }));
        var firstPage=Assert.Single(database.GetPendingCatalogRetirementAnchors("a",scope,limit:1));
        Assert.Equal("image",firstPage.MediaKind);
        Assert.Equal(expectedFingerprint,firstPage.Fingerprint);
        var next=database.GetPendingCatalogRetirementAnchors("a",scope,firstPage.Cursor,limit:1);
        if(album) Assert.Empty(next);
        else
        {
            var video=Assert.Single(next);Assert.Equal("video",video.MediaKind);
            Assert.Empty(database.GetPendingCatalogRetirementAnchors("a",scope,video.Cursor));
        }
        Assert.Equal(album?1:2,database.GetPendingCatalogRetirementAnchors("a",scope).Count);
        Assert.Equal(album?0:1,database.GetPendingCatalogRetirementAnchors("a",scope with { Photos=false }).Count);
        using(var rebaseline=database.Command("UPDATE sync_publishers SET state='rebaselining' WHERE publisher_id='a'")) rebaseline.ExecuteNonQuery();
        Assert.Empty(database.GetPendingCatalogRetirementAnchors("a",scope));
        using(var resume=database.Command("UPDATE sync_publishers SET state=$state WHERE publisher_id='a'"))
        { resume.Parameters.AddWithValue("$state",receiving?"receiving":"publishing");resume.ExecuteNonQuery(); }
        using(var change=database.Command("UPDATE sync_entity_identities SET global_id='new-lifecycle' WHERE publisher_id='a' AND kind=$kind"))
        { change.Parameters.AddWithValue("$kind",kind);change.ExecuteNonQuery(); }
        Assert.Empty(database.GetPendingCatalogRetirementAnchors("a",scope));
        using(var restore=database.Command("UPDATE sync_entity_identities SET global_id='catalog-a' WHERE publisher_id='a' AND kind=$kind"))
        { restore.Parameters.AddWithValue("$kind",kind);restore.ExecuteNonQuery(); }
        Assert.Equal(album?1:2,database.GetPendingCatalogRetirementAnchors("a",scope).Count);
        Assert.True(new ImageRecordPurger(database).Purge(10,requireMissing:false));
        using(var removed=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_media WHERE media_id=10")) Assert.Equal(0L,removed.ExecuteScalar());
        using(var removed=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_baselines WHERE media_global_id='image-a'")) Assert.Equal(0L,removed.ExecuteScalar());
        Assert.NotNull(database.GetImage(20));
        Assert.Equal(album?0:1,database.GetPendingCatalogRetirementAnchors("a",scope).Count);
        if(receiving)
        {
            using var revoke=database.Command("DELETE FROM sync_source_bindings WHERE publisher_id='a'");revoke.ExecuteNonQuery();
            Assert.Empty(database.GetPendingCatalogRetirementAnchors("a",scope));
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();if(Directory.Exists(_root)) Directory.Delete(_root,true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersonMergeCapturesOriginalMediaBeforeMovingAssignmentsUnlessRemoteApplyIsSuppressed(bool suppressed)
    {
        using var database=new PicerDatabase(Path.Combine(_root,"picer.db"));
        var source=database.AddSource(@"C:\Photos");
        database.UpsertMirroredImage(new ImageRecord { Id=70,SourceId=source.Id,Path=@"C:\Photos\one.jpg",FileSize=10,
            ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
        database.UpsertMirroredFace(new(80,70,"test-model",0,0,0,10,10,0.9,null,null,DateTime.UtcNow.ToString("O")));
        database.UpsertMirroredPerson(new(90,"旧测试人物",0,0));database.UpsertMirroredPerson(new(91,"新测试人物",0,0));
        var people=new Picer.Core.People.PeopleService(database);people.ConfirmFace(80,90);
        using(var seed=database.Command("""
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
            VALUES('a','library','device','grant','space','publishing','epoch');
            INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id)
            VALUES('a','person','90','person-a'),('a','image','70','image-a');
            """)) seed.ExecuteNonQuery();
        if(suppressed) { using var guard=database.Command("INSERT INTO sync_suppression(id) VALUES(1)");guard.ExecuteNonQuery(); }
        var expectedFingerprint=database.GetCatalogRetirementFingerprint("person",90,"image",70,publisher:"a");
        people.Merge(90,91);
        Assert.Null(people.Get(90));Assert.Equal(91,Assert.Single(database.GetAllMirroredFaceAssignments()).PersonId);
        using var count=database.Command("SELECT COUNT(*) FROM sync_catalog_retirement_media WHERE media_kind='image' AND media_id=70");
        Assert.Equal(suppressed?0L:1L,count.ExecuteScalar());
        Assert.Empty(database.GetPendingCatalogRetirementAnchors("a",new(new[]{source.Id})));
        Assert.Equal(suppressed?0:1,database.GetPendingCatalogRetirementAnchors("a",new(new[]{source.Id},People:true)).Count);
        if(!suppressed) Assert.Equal(expectedFingerprint,Assert.Single(database.GetPendingCatalogRetirementAnchors("a",new(new[]{source.Id},People:true))).Fingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCatalogDeletionRollsBackItsMediaRelationships(bool album)
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-catalog-rollback-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var database=new PicerDatabase(Path.Combine(root,"picer.db"));
            var source=database.AddSource(root);
            var image=database.UpsertImage(new ImageRecord { SourceId=source.Id,Path=Path.Combine(root,"photo.jpg"),
                FileSize=10,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
            var id=album?SemanticEngine.AddAlbum(database,"测试相册").Id:SemanticEngine.AddTag(database,"测试标签").Id;
            if(album) SemanticEngine.AddToAlbum(database,id,image.Id);else SemanticEngine.AssignTag(database,image.Id,id);
            using(var fail=database.Command($"CREATE TRIGGER test_catalog_abort BEFORE DELETE ON {(album?"albums":"tags")} BEGIN SELECT RAISE(ABORT,'test_catalog_delete_failed'); END"))
                fail.ExecuteNonQuery();
            void Delete() { if(album) SemanticEngine.DeleteAlbum(database,id);else SemanticEngine.DeleteTag(database,id); }
            Assert.Throws<SqliteException>(Delete);
            if(album) Assert.Equal(image.Id,Assert.Single(SemanticEngine.GetAlbumImages(database,id)).Id);
            else Assert.Equal(id,Assert.Single(SemanticEngine.GetImageTags(database,image.Id)).Id);
            using(var allow=database.Command("DROP TRIGGER test_catalog_abort")) allow.ExecuteNonQuery();
            Delete();
            if(album) Assert.Empty(SemanticEngine.GetAlbums(database));else Assert.Empty(SemanticEngine.GetTags(database));
            Assert.NotNull(database.GetImage(image.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();if(Directory.Exists(root)) Directory.Delete(root,true);
        }
    }
}
