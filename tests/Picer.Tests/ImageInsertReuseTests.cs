using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Tests;

public sealed class ImageInsertReuseTests
{
    [Fact]
    public void BulkInsertionSurvivesRollbackAndKeepsPathUpdatesAtomic()
    {
        using var database=new PicerDatabase(":memory:");
        var source=database.AddSource(@"C:\Photos");
        ImageRecord Image(string name,long size)=>new(){SourceId=source.Id,Path=@"C:\Photos\"+name,FileSize=size,
            ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow};
        using(var rollback=database.BeginTransaction())database.UpsertImageInTransaction(Image("one.jpg",10),rollback);
        Assert.Empty(database.GetImages());
        using(var commit=database.BeginTransaction())
        {
            database.UpsertImageInTransaction(Image("one.jpg",20),commit);
            database.UpsertImageInTransaction(Image("two.jpg",30),commit);
            database.UpsertImageInTransaction(Image("one.jpg",40),commit);
            commit.Commit();
        }
        Assert.Equal(2,database.GetImages().Count);
        Assert.Equal(40,database.GetImageByPath(@"C:\Photos\one.jpg")!.FileSize);
        Assert.Equal(30,database.GetImageByPath(@"C:\Photos\two.jpg")!.FileSize);
    }
}
