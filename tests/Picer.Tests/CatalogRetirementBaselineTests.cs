using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.Tests;

public sealed class CatalogRetirementBaselineTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"picer-catalog-baseline-"+Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingSourceOperationUsesTheSameFingerprintBeforeAndAfterItsReceipt(bool album)
    {
        using var local=new PicerDatabase(Path.Combine(_root,"local.db"));
        using var remote=new PicerDatabase(Path.Combine(_root,"remote.db"));
        var localCatalog=Seed(local,10,album);var remoteCatalog=Seed(remote,20,album);
        using(var publisher=local.Command("""
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
            VALUES('publisher','library','device','grant','space','publishing','epoch')
            """)) publisher.ExecuteNonQuery();
        var operation=Assert.Single(local.GetMetadataEdits()).OperationId;
        var accepted=Picer.Core.Identity.DeviceAuthorization.Digest("publisher:"+operation)[..32];
        using(var accept=remote.Command("UPDATE metadata_edits SET operation_id=$operation,server_version=55"))
        { accept.Parameters.AddWithValue("$operation",accepted);accept.ExecuteNonQuery(); }
        var kind=album?"album":"tag";
        var expected=remote.GetCatalogRetirementFingerprint(kind,remoteCatalog,"image",20);
        Assert.Equal(expected,local.GetCatalogRetirementFingerprint(kind,localCatalog,"image",10,publisher:"publisher"));
        using(var receipt=local.Command("UPDATE metadata_edits SET operation_id=$operation,server_version=55"))
        { receipt.Parameters.AddWithValue("$operation",accepted);receipt.ExecuteNonQuery(); }
        Assert.Equal(expected,local.GetCatalogRetirementFingerprint(kind,localCatalog,"image",10,publisher:"publisher"));
        if(album) { SemanticEngine.RemoveFromAlbum(local,localCatalog,10);SemanticEngine.AddToAlbum(local,localCatalog,10); }
        else { SemanticEngine.RemoveTag(local,10,localCatalog);SemanticEngine.AssignTag(local,10,localCatalog); }
        Assert.NotEqual(expected,local.GetCatalogRetirementFingerprint(kind,localCatalog,"image",10,publisher:"publisher"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BaselineSurvivesRemappingAndVersionPromotionButDetectsNewDecisions(bool album)
    {
        using var first=new PicerDatabase(Path.Combine(_root,"first.db"));
        using var second=new PicerDatabase(Path.Combine(_root,"second.db"));
        var a=Seed(first,10,album);var b=Seed(second,20,album);
        var kind=album?"album":"tag";
        var fieldKind=album?"album-link":"tag-link";
        var operation=Assert.Single(first.GetMetadataEdits()).OperationId;
        using(var remap=second.Command("UPDATE metadata_edits SET operation_id=$operation,server_version=55 WHERE kind=$kind"))
        { remap.Parameters.AddWithValue("$operation",operation);remap.Parameters.AddWithValue("$kind",fieldKind);remap.ExecuteNonQuery(); }
        var fingerprint=first.GetCatalogRetirementFingerprint(kind,a,"image",10);
        Assert.Equal(fingerprint,second.GetCatalogRetirementFingerprint(kind,b,"image",20));
        if(album) { SemanticEngine.RemoveFromAlbum(second,b,20);SemanticEngine.AddToAlbum(second,b,20); }
        else { SemanticEngine.RemoveTag(second,20,b);SemanticEngine.AssignTag(second,20,b); }
        Assert.NotEqual(fingerprint,second.GetCatalogRetirementFingerprint(kind,b,"image",20));
    }

    [Fact]
    public void LegacyRetirementDoesNotInventMissingPreDeleteEvidence()
    {
        var path=Path.Combine(_root,"legacy.db");
        using(var database=new PicerDatabase(path))
        {
            var tag=Seed(database,10,false);
            using(var seed=database.Command("""
                INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
                VALUES('a','library','device','grant','space','publishing','epoch');
                INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id)
                VALUES('a','tag',$tag,'tag-a'),('a','image','10','image-a');
                """))
            { seed.Parameters.AddWithValue("$tag",tag.ToString(System.Globalization.CultureInfo.InvariantCulture));seed.ExecuteNonQuery(); }
            SemanticEngine.DeleteTag(database,tag);
            using var downgrade=database.Command("""
                DROP TABLE sync_catalog_retirement_baselines;
                ALTER TABLE sync_catalog_retirements DROP COLUMN sealed_metadata_revision;
                ALTER TABLE sync_catalog_retirements DROP COLUMN captured_media_count;
                ALTER TABLE sync_catalog_retirement_receipts DROP COLUMN applied_operations;
                PRAGMA user_version=77;
                """);downgrade.ExecuteNonQuery();
        }
        using var migrated=new PicerDatabase(path);
        var scope=new Picer.Core.Identity.SpaceUploadScope(migrated.GetSources().Select(s=>s.Id).ToArray());
        Assert.Null(Assert.Single(migrated.GetPendingCatalogRetirementAnchors("a",scope)).Fingerprint);
        Assert.Empty(migrated.GetPendingCatalogRetirementAnchors("a",scope,requirePublishedCut:true));
        using(var evidence=migrated.Command("SELECT sealed_metadata_revision,captured_media_count FROM sync_catalog_retirements"))
        using(var row=evidence.ExecuteReader())
        { Assert.True(row.Read());Assert.True(row.IsDBNull(0));Assert.True(row.IsDBNull(1)); }
        using(var replay=migrated.BeginTransaction())
        {
            using var catalog=migrated.Command("SELECT catalog_id FROM sync_catalog_retirements LIMIT 1");
            migrated.RecordCatalogRetirement("tag",Convert.ToInt64(catalog.ExecuteScalar()),replay);
            replay.Commit();
        }
        Assert.Null(Assert.Single(migrated.GetPendingCatalogRetirementAnchors("a",scope)).Fingerprint);
        using var version=migrated.Command("PRAGMA user_version");Assert.Equal((long)PicerDatabase.CurrentSchemaVersion,version.ExecuteScalar());
    }

    private static long Seed(PicerDatabase database,long imageId,bool album)
    {
        var source=database.AddSource(@"C:\Photos");
        database.UpsertMirroredImage(new ImageRecord { Id=imageId,SourceId=source.Id,Path=@"C:\Photos\one.jpg",
            FileSize=10,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
        // Give each side distinct catalogue IDs as well as distinct media IDs.
        if(imageId==20) { if(album) SemanticEngine.AddAlbum(database,"占位");else SemanticEngine.AddTag(database,"占位"); }
        var id=album?SemanticEngine.AddAlbum(database,"测试").Id:SemanticEngine.AddTag(database,"测试").Id;
        if(album) SemanticEngine.AddToAlbum(database,id,imageId);else SemanticEngine.AssignTag(database,imageId,id);
        return id;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();if(Directory.Exists(_root)) Directory.Delete(_root,true);
    }
}
