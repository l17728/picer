using System.Diagnostics;
using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Tests;

public sealed class MetadataPagingPerformanceTests
{
    [Fact]
    public void VersionPageUsesTargetIdentityLookupAcrossLargePublisher()
    {
        using var database=new PicerDatabase(":memory:");var source=database.AddSource(@"C:\Photos");
        database.UpsertMirroredImage(new ImageRecord{Id=20000,SourceId=source.Id,Path=@"C:\Photos\one.jpg",FileSize=10,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow});
        using(var seed=database.Command("""
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
            VALUES('publisher','library','device','grant','space','receiving','epoch');
            WITH RECURSIVE ids(n) AS(SELECT 1 UNION ALL SELECT n+1 FROM ids WHERE n<20000)
            INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id)
                SELECT 'publisher','image',CAST(n AS TEXT),lower(hex(randomblob(16))),n FROM ids;
            WITH RECURSIVE ids(n) AS(SELECT 1 UNION ALL SELECT n+1 FROM ids WHERE n<500)
            INSERT INTO tags(id,name,created_utc) SELECT n,'tag-'||n,'2026-10-26T00:00:00.0000000Z' FROM ids;
            INSERT INTO image_tags(image_id,tag_id) SELECT 20000,id FROM tags;
            INSERT INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id,server_version,base_version)
                SELECT 'tag-link',20000,CAST(id AS TEXT),'1','2026-10-26T00:00:00.0000000Z',lower(hex(randomblob(16))),id,0 FROM tags;
            """))seed.ExecuteNonQuery();
        using var transaction=database.BeginReadTransaction();
        var watch=Stopwatch.StartNew();
        var rows=database.ReadVersionedMetadataEdits("publisher",0,"",500,501,transaction);
        watch.Stop();
        Assert.Equal(500,rows.Count);Assert.Equal(500,rows[^1].ServerVersion);
        Assert.Empty(database.ReadVersionedMetadataEdits("another-publisher",0,"",500,501,transaction));
        Assert.True(watch.Elapsed<TimeSpan.FromSeconds(2),$"500 edits over 20k identities took {watch.Elapsed.TotalMilliseconds:F0} ms");
    }
}
