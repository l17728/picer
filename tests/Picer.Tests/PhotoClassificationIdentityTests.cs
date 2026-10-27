using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Models;

namespace Picer.Tests;

public sealed class PhotoClassificationIdentityTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"picer-photo-binding-"+Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    public PhotoClassificationIdentityTests()=>_database=new(Path.Combine(_root,"picer.db"));

    [Fact]
    public void UnhashedOperationsCannotFollowAChangedFileAndHashLearningKeepsAnUnchangedFilesOperation()
    {
        var id=Add('a',"first",null);var operation=Bind(id);var original=_database.GetPhotoClassificationContentIdentity(id);
        Assert.Equal(id,_database.ResolvePhotoClassificationOperation(operation));
        using(var change=_database.Command("UPDATE images SET file_size=11 WHERE id=$id"))
        { change.Parameters.AddWithValue("$id",id);change.ExecuteNonQuery(); }
        Assert.NotEqual(original,_database.GetPhotoClassificationContentIdentity(id));
        Assert.Null(_database.ResolvePhotoClassificationOperation(operation));
        var current=Bind(id);
        using(var hash=_database.Command("UPDATE images SET sha256=$sha WHERE id=$id"))
        { hash.Parameters.AddWithValue("$sha",new string('a',64));hash.Parameters.AddWithValue("$id",id);hash.ExecuteNonQuery(); }
        Assert.Equal(id,_database.ResolvePhotoClassificationOperation(current));
        Assert.Null(_database.ResolvePhotoClassificationOperation(operation));
        Assert.Equal("sha256:"+new string('a',64),_database.GetPhotoClassificationContentIdentity(id));
    }

    [Fact]
    public void CanonicalReferenceAndPurgeNeverTransferAnOperationToAnotherOwner()
    {
        var first=Add('a',"first",new string('a',64));var second=Add('a',"second",new string('a',64));
        var foreign=Add('b',"foreign",new string('a',64));var operation=Bind(second);
        Assert.Equal(first,_database.ResolvePhotoClassificationOperation(operation));
        Assert.True(new ImageRecordPurger(_database).Purge(second,requireMissing:false));
        Assert.Equal(first,_database.ResolvePhotoClassificationOperation(operation));
        Assert.True(new ImageRecordPurger(_database).Purge(first,requireMissing:false));
        Assert.Null(_database.ResolvePhotoClassificationOperation(operation));
        Assert.NotNull(_database.GetImage(foreign));
        using var count=_database.Command("SELECT COUNT(*) FROM photo_metadata_operation_bindings");Assert.Equal(0L,count.ExecuteScalar());
    }

    [Fact]
    public void AVerifiedContentReplacementDoesNotRebindAnObservedOperation()
    {
        var id=Add('a',"first",new string('a',64));var operation=Bind(id);
        using(var change=_database.Command("UPDATE images SET sha256=$sha WHERE id=$id"))
        { change.Parameters.AddWithValue("$sha",new string('b',64));change.Parameters.AddWithValue("$id",id);change.ExecuteNonQuery(); }
        using(var transaction=_database.BeginTransaction())
        {
            Assert.Equal("sha256:"+new string('a',64),_database.BindPhotoClassificationOperation(operation,id,transaction));
            transaction.Commit();
        }
        Assert.Null(_database.ResolvePhotoClassificationOperation(operation));
    }

    [Fact]
    public void RenamingAnUnchangedFileKeepsItsUnknownContentOperation()
    {
        var id=Add('a',"first",null);var operation=Bind(id);var original=_database.GetPhotoClassificationContentIdentity(id);
        using(var rename=_database.Command("UPDATE images SET path=$path WHERE id=$id"))
        {
            rename.Parameters.AddWithValue("$path",PublishedMediaPath.Qualify(new string('a',32),@"C:\Photos\renamed.jpg"));
            rename.Parameters.AddWithValue("$id",id);rename.ExecuteNonQuery();
        }
        Assert.Equal(original,_database.GetPhotoClassificationContentIdentity(id));
        Assert.Equal(id,_database.ResolvePhotoClassificationOperation(operation));
    }

    [Fact]
    public void AnOperationCannotBeReusedForAnotherOwnerOrUnrelatedContent()
    {
        var first=Add('a',"first",new string('a',64));var operation=Bind(first);
        var foreign=Add('b',"foreign",new string('a',64));var unrelated=Add('a',"unrelated",new string('b',64));
        var duplicate=Add('a',"duplicate",new string('a',64));
        using var transaction=_database.BeginTransaction();
        Assert.Throws<InvalidDataException>(()=>_database.BindPhotoClassificationOperation(operation,foreign,transaction));
        Assert.Throws<InvalidDataException>(()=>_database.BindPhotoClassificationOperation(operation,unrelated,transaction));
        Assert.Equal("sha256:"+new string('a',64),_database.BindPhotoClassificationOperation(operation,duplicate,transaction));
        transaction.Commit();
        Assert.Equal(first,_database.ResolvePhotoClassificationOperation(operation));
    }

    [Fact]
    public void LearningAHashWhileChangingFileVersionCannotPromoteAnOldOperation()
    {
        var id=Add('a',"first",null);var operation=Bind(id);
        using(var change=_database.Command("UPDATE images SET sha256=$sha,file_size=11 WHERE id=$id"))
        {
            change.Parameters.AddWithValue("$sha",new string('a',64));change.Parameters.AddWithValue("$id",id);change.ExecuteNonQuery();
        }
        Assert.Null(_database.ResolvePhotoClassificationOperation(operation));
        Assert.Equal(id,_database.ResolvePhotoClassificationOperation(Bind(id)));
    }

    [Fact]
    public void Version73UpgradeCreatesDurableUnknownFileIdentityAndOperationSurvivesReopen()
    {
        var id=Add('a',"first",null);var path=_database.DatabasePath;
        using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+path))
        {
            connection.Open();LegacySchemaFixture.RevertPhotoClassificationIdentity(connection);
            using var version=connection.CreateCommand();version.CommandText="PRAGMA user_version=73";version.ExecuteNonQuery();
        }
        _database.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var operation=Guid.NewGuid().ToString("N");string identity;
        using(var migrated=new PicerDatabase(path))
        {
            identity=migrated.GetPhotoClassificationContentIdentity(id);Assert.StartsWith("local:",identity);
            using var transaction=migrated.BeginTransaction();
            Assert.Equal(identity,migrated.BindPhotoClassificationOperation(operation,id,transaction));transaction.Commit();
        }
        using var reopened=new PicerDatabase(path);
        Assert.Equal(identity,reopened.GetPhotoClassificationContentIdentity(id));
        Assert.Equal(id,reopened.ResolvePhotoClassificationOperation(operation));
    }

    private string Bind(long id)
    {
        var operation=Guid.NewGuid().ToString("N");using var transaction=_database.BeginTransaction();
        _database.BindPhotoClassificationOperation(operation,id,transaction);transaction.Commit();return operation;
    }

    private long Add(char owner,string name,string? hash)
    {
        var root=PublishedMediaPath.Qualify(new string(owner,32),@"C:\Photos");var source=_database.AddSource(root);
        return _database.UpsertImage(new ImageRecord { SourceId=source.Id,Path=root+"\\"+name+".jpg",Sha256=hash,
            FileSize=10,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow }).Id;
    }

    public void Dispose()
    {
        _database.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(_root,true);
    }
}
