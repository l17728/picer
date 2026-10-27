using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.Core.People;

namespace Picer.Tests;

public sealed class PhotoContentOwnershipTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"picer-photo-owner-"+Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    public PhotoContentOwnershipTests()=>_database=new(Path.Combine(_root,"picer.db"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContentRestorationCannotCopyManualFaceIdentityToAnotherPublisher(bool sameOwner)
    {
        var firstRoot=PublishedMediaPath.Qualify(new string('a',32),@"C:\Photos");
        var secondRoot=PublishedMediaPath.Qualify(new string(sameOwner?'a':'b',32),@"C:\OtherPhotos");
        var firstSource=_database.AddSource(firstRoot);var secondSource=_database.AddSource(secondRoot);
        var first=AddImage(firstSource.Id,firstRoot+@"\one.jpg");
        var person=new PeopleService(_database).Create("来源隔离测试人物");
        using(var seed=_database.Command("""
            INSERT INTO detected_faces(id,image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc)
            VALUES(101,$image,'test-model',0,0,0,10,10,0.9,'2026-10-24');
            INSERT INTO face_person_assignments(face_id,person_id,state,confidence,assigned_by,updated_utc)
            VALUES(101,$person,'confirmed',1,'user','2026-10-24');
            INSERT INTO face_corrections(face_id,new_person_id,action,created_utc)
            VALUES(101,$person,'confirm','2026-10-24');
            """))
        {
            seed.Parameters.AddWithValue("$image",first);seed.Parameters.AddWithValue("$person",person.Id);seed.ExecuteNonQuery();
        }
        var second=AddImage(secondSource.Id,secondRoot+@"\two.jpg");
        using var assignments=_database.Command("SELECT COUNT(*) FROM face_person_assignments a JOIN detected_faces f ON f.id=a.face_id WHERE f.image_id=$image");
        assignments.Parameters.AddWithValue("$image",second);
        Assert.Equal(sameOwner?1L:0L,assignments.ExecuteScalar());
        using var corrections=_database.Command("SELECT COUNT(*) FROM face_corrections c JOIN detected_faces f ON f.id=c.face_id WHERE f.image_id=$image");
        corrections.Parameters.AddWithValue("$image",second);Assert.Equal(sameOwner?1L:0L,corrections.ExecuteScalar());
        Assert.True(new ImageRecordPurger(_database).Purge(first,requireMissing:false));
        var third=AddImage(firstSource.Id,firstRoot+@"\three.jpg");
        assignments.Parameters["$image"].Value=third;
        Assert.Equal(sameOwner?1L:0L,assignments.ExecuteScalar());
    }

    private long AddImage(long source,string path)=>_database.UpsertImage(new ImageRecord
    {
        SourceId=source,Path=path,FileSize=10,Sha256=new string('a',64),ContentSha256=new string('a',64),
        ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow,Status=ImageStatus.Active
    }).Id;

    [Fact]
    public void UpgradeFrom72ScopesExistingDecisionsAndDoesNotUseLegacyUnscopedCacheForForeignCopies()
    {
        var root=PublishedMediaPath.Qualify(new string('a',32),@"C:\Photos");
        var source=_database.AddSource(root);var first=AddImage(source.Id,root+@"\one.jpg");
        var person=new PeopleService(_database).Create("迁移测试人物");
        using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+_database.DatabasePath))
        {
            connection.Open();LegacySchemaFixture.RevertPhotoFaceCacheOwnership(connection);
        }
        using(var seed=_database.Command("""
            INSERT INTO detected_faces(id,image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc)
            VALUES(101,$image,'test-model',0,0,0,10,10,0.9,'2026-10-24');
            INSERT INTO face_person_rejections(face_id,person_id,created_utc) VALUES(101,$person,'2026-10-24');
            INSERT INTO face_review_states(face_id,state,updated_utc) VALUES(101,'ignored','2026-10-24');
            PRAGMA user_version=72;
            """))
        {
            seed.Parameters.AddWithValue("$image",first);seed.Parameters.AddWithValue("$person",person.Id);seed.ExecuteNonQuery();
        }
        var path=_database.DatabasePath;_database.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var migrated=new PicerDatabase(path);
        foreach(var owner in new[]{'a','b'})
        {
            var targetRoot=PublishedMediaPath.Qualify(new string(owner,32),@"C:\Copies");
            var targetSource=migrated.AddSource(targetRoot);
            var target=migrated.UpsertImage(new ImageRecord { SourceId=targetSource.Id,Path=targetRoot+@"\copy.jpg",
                Sha256=new string('a',64),ContentSha256=new string('a',64),FileSize=10,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow }).Id;
            foreach(var table in new[]{"face_person_rejections","face_review_states"})
            {
                using var count=migrated.Command($"SELECT COUNT(*) FROM {table} r JOIN detected_faces f ON f.id=r.face_id WHERE f.image_id=$id");
                count.Parameters.AddWithValue("$id",target);Assert.Equal(owner=='a'?1L:0L,count.ExecuteScalar());
            }
        }
    }

    public void Dispose()
    {
        _database.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(_root,true);
    }
}
