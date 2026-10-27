using Microsoft.Data.Sqlite;
using Picer.Core.Data;

namespace Picer.Tests;

public sealed class CatalogCompletionInboxTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"picer-completion-inbox-"+Guid.NewGuid().ToString("N"));

    [Fact]
    public void ManifestSurvivesRestartAndCannotBeAcknowledgedBeforeRemoteCompletion()
    {
        var path=Path.Combine(_root,"picer.db");var epoch=Guid.NewGuid().ToString("N");
        var manifest=new CatalogRetirementManifest(Guid.NewGuid().ToString("N"),"tag",Guid.NewGuid().ToString("N"),0);
        using(var database=new PicerDatabase(path))
        {
            using(var seed=database.Command("""
                INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
                VALUES('publisher','library','device','grant','space','publishing',$epoch)
                """))
            { seed.Parameters.AddWithValue("$epoch",epoch);seed.ExecuteNonQuery(); }
            using var transaction=database.BeginTransaction();
            database.StageCatalogCompletions("publisher",epoch,[manifest],transaction);transaction.Commit();
        }
        using var reopened=new PicerDatabase(path);
        Assert.Equal(manifest,Assert.Single(reopened.GetPendingCatalogCompletions("publisher",epoch)));
        Assert.Empty(reopened.GetPendingCatalogCompletions("other",epoch));
        Assert.Empty(reopened.GetPendingCatalogCompletions("publisher",Guid.NewGuid().ToString("N")));
        using(var transaction=reopened.BeginTransaction())
        {
            reopened.StageCatalogCompletions("publisher",epoch,[manifest],transaction);
            Assert.Throws<InvalidDataException>(()=>reopened.StageCatalogCompletions("publisher",epoch,[manifest with { MediaCount=1 }],transaction));
            Assert.Throws<InvalidDataException>(()=>reopened.StageCatalogCompletions("publisher",Guid.NewGuid().ToString("N"),[manifest],transaction));
            Assert.Throws<ArgumentException>(()=>reopened.RecordCatalogCompletionInboxResult("publisher",epoch,manifest.OperationId,new("completed",true),false,transaction));
            reopened.RecordCatalogCompletionInboxResult("publisher",epoch,manifest.OperationId,new("conflict",false),true,transaction);
            reopened.RecordCatalogCompletionInboxResult("publisher",epoch,manifest.OperationId,new("pending",false),false,transaction);
            transaction.Commit();
        }
        using(var query=reopened.Command("SELECT remote_completed FROM sync_catalog_completion_inbox")) Assert.Equal(1L,query.ExecuteScalar());
        Assert.Single(reopened.GetPendingCatalogCompletions("publisher",epoch));
        using(var transaction=reopened.BeginTransaction())
        {
            reopened.RecordCatalogCompletionInboxResult("publisher",epoch,manifest.OperationId,new("completed",true),true,transaction);
            transaction.Commit();
        }
        Assert.Empty(reopened.GetPendingCatalogCompletions("publisher",epoch));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();if(Directory.Exists(_root)) Directory.Delete(_root,true);
    }
}
