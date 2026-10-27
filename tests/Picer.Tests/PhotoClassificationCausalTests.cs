using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class PhotoClassificationCausalTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"picer-photo-classification-causal-"+Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    private readonly long _first;
    private readonly long _duplicate;
    private readonly long _foreign;
    public PhotoClassificationCausalTests()
    {
        _database=new(Path.Combine(_root,"picer.db"));
        _first=Add('a',"one");_duplicate=Add('a',"two");_foreign=Add('b',"foreign");
    }

    [Fact]
    public void ConfirmRejectAndRestoreProduceIndependentConfirmationAndPolicyFields()
    {
        var repository=new VisualConceptRepository(_database);
        repository.Confirm(_first,"scene.mountain");
        Assert.Equal("1",Assert.Single(_database.GetMetadataEdits().Where(edit=>edit.Kind=="photo-classification-confirmation")).Value);
        repository.Reject(_first,"scene.mountain");
        Assert.Equal("rejected",Assert.Single(_database.GetMetadataEdits().Where(edit=>edit.Kind=="photo-classification-policy")).Value);
        Assert.Equal(2,IndexSearchEngine.Search(_database,new SearchFilters { VisualConceptIds=["scene.mountain"] }).Count);
        new ClassificationProjectionService(_database).RestoreSuggestion(_first,"scene.mountain");
        Assert.Equal("restored",Assert.Single(_database.GetMetadataEdits().Where(edit=>edit.Kind=="photo-classification-policy")).Value);
        Assert.Equal("1",Assert.Single(_database.GetMetadataEdits().Where(edit=>edit.Kind=="photo-classification-confirmation")).Value);
    }

    [Fact]
    public void ReanalysisDoesNotPromoteARestoredSuggestionOrResurrectARejectedCategory()
    {
        var projection=new ClassificationProjectionService(_database);
        var repository=new VisualConceptRepository(_database);
        repository.Reject(_first,"scene.mountain");
        projection.RestoreSuggestion(_first,"scene.mountain");
        repository.Reject(_first,"scene.beach");
        var run=projection.BeginOrResume();
        projection.ProcessNextBatch(run.RunId,10,semanticResults:new[]{_first,_duplicate,_foreign}.ToDictionary(id=>id,
            _=>new SemanticAnalysisResult([new VisualConceptScore("scene.mountain",0.99f),new VisualConceptScore("scene.beach",0.99f)])));
        foreach(var concept in new[]{"scene.mountain","scene.beach"})
            Assert.Equal(_foreign,Assert.Single(IndexSearchEngine.Search(_database,new SearchFilters { VisualConceptIds=[concept] })).Id);
        Assert.Equal(3,IndexSearchEngine.Search(_database,new SearchFilters { VisualConceptIds=["scene.mountain"],IncludeSuggestedClassifications=true }).Count);
    }

    [Fact]
    public void ConcurrentDecisionsOnDuplicateReferencesShareOneCausalBaselineAndKeepConflict()
    {
        var initial=Assert.Single(_database.AcceptMetadataEdits([Policy(_first,"accepted")]));
        var winner=Assert.Single(_database.AcceptMetadataEdits([Policy(_duplicate,"rejected",initial.ServerVersion)]));
        var stale=Policy(_first,"restored",initial.ServerVersion);
        var result=Assert.Single(_database.AcceptMetadataEdits([stale]));
        Assert.Equal(winner.OperationId,result.OperationId);
        Assert.Single(_database.GetMetadataEditConflicts());
        var snapshot=_database.GetMetadataConflictSnapshot();
        Assert.Equal("恢复为待确认",Assert.Single(snapshot.Reviews).ProposedDisplay);
        Assert.Equal("不是此类",Assert.Single(snapshot.Reviews).CurrentDisplay);
        _database.ResolveMetadataEditConflict(stale.OperationId,true,winner.ServerVersion,winner.OperationId,true,snapshot.Epoch);
        Assert.Equal("restored",_database.GetMetadataEdits().Single(edit=>edit.Kind=="photo-classification-policy").Value);
        var foreign=Assert.Single(_database.AcceptMetadataEdits([Policy(_foreign,"restored")]));
        Assert.NotEqual(winner.OperationId,foreign.OperationId);
    }

    [Fact]
    public void PurgingCanonicalReferenceTransfersItsFieldsOnlyUntilLastOwnedReference()
    {
        new VisualConceptRepository(_database).Confirm(_first,"scene.mountain");
        Assert.NotEmpty(_database.GetMetadataEdits().Where(edit=>edit.Kind.StartsWith("photo-classification-",StringComparison.Ordinal)));
        Assert.True(new ImageRecordPurger(_database).Purge(_first,requireMissing:false));
        var fields=_database.GetMetadataEdits().Where(edit=>edit.Kind.StartsWith("photo-classification-",StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(fields);Assert.All(fields,edit=>Assert.Equal(_duplicate,edit.EntityId));
        Assert.True(new ImageRecordPurger(_database).Purge(_duplicate,requireMissing:false));
        Assert.Empty(_database.GetMetadataEdits().Where(edit=>edit.Kind.StartsWith("photo-classification-",StringComparison.Ordinal)));
        Assert.NotNull(_database.GetImage(_foreign));
    }

    [Fact]
    public void AcceptedOperationCanBeRetriedAfterItsOriginalReferenceWasPurged()
    {
        var request=Policy(_first,"rejected");
        var initial=Assert.Single(_database.AcceptMetadataEdits([request]));
        Assert.True(new ImageRecordPurger(_database).Purge(_first,requireMissing:false));
        var retry=Assert.Single(_database.AcceptMetadataEdits([request]));
        Assert.Equal(initial.OperationId,retry.OperationId);Assert.Equal(_duplicate,retry.EntityId);
        Assert.True(new ImageRecordPurger(_database).Purge(_duplicate,requireMissing:false));
        using var receipts=_database.Command("SELECT COUNT(*) FROM metadata_causal_receipts WHERE kind='photo-classification-policy'");
        Assert.Equal(0L,receipts.ExecuteScalar());
    }

    [Fact]
    public void EditingReplacementContentKeepsOriginalDecisionForSurvivingCopy()
    {
        var repository=new VisualConceptRepository(_database);
        repository.Reject(_first,"scene.mountain");
        var original=Assert.Single(_database.GetMetadataEdits());
        using(var replace=_database.Command("UPDATE images SET sha256=$sha,file_size=11 WHERE id=$id"))
        {
            replace.Parameters.AddWithValue("$sha",new string('c',64));replace.Parameters.AddWithValue("$id",_first);
            replace.ExecuteNonQuery();
        }
        new ClassificationProjectionService(_database).RestoreSuggestion(_first,"scene.mountain");
        var fields=_database.GetMetadataEdits().Where(edit=>edit.Kind=="photo-classification-policy").ToArray();
        Assert.Equal(2,fields.Length);
        Assert.Contains(fields,edit=>edit.EntityId==_duplicate && edit.OperationId==original.OperationId && edit.Value=="rejected");
        Assert.Contains(fields,edit=>edit.EntityId==_first && edit.Value=="restored");
    }

    [Fact]
    public void SwappedContentsKeepBothDecisionsWithoutRecursiveFieldCollision()
    {
        void Replace(long id,char hash)
        {
            using var update=_database.Command("UPDATE images SET sha256=$sha WHERE id=$id");
            update.Parameters.AddWithValue("$sha",new string(hash,64));update.Parameters.AddWithValue("$id",id);update.ExecuteNonQuery();
        }
        Replace(_duplicate,'c');
        var repository=new VisualConceptRepository(_database);
        repository.Reject(_first,"scene.mountain");
        new ClassificationProjectionService(_database).RestoreSuggestion(_duplicate,"scene.mountain");
        Replace(_first,'c');Replace(_duplicate,'a');
        repository.Reject(_first,"scene.mountain");
        var fields=_database.GetMetadataEdits().Where(edit=>edit.Kind=="photo-classification-policy").ToArray();
        Assert.Equal(2,fields.Length);Assert.All(fields,edit=>Assert.Equal("rejected",edit.Value));
    }

    [Fact]
    public void LearningDuplicateHashesKeepsContradictoryOfflineDecisionsForReview()
    {
        using(var unknown=_database.Command("UPDATE images SET sha256=NULL WHERE id IN ($first,$second)"))
        {
            unknown.Parameters.AddWithValue("$first",_first);unknown.Parameters.AddWithValue("$second",_duplicate);unknown.ExecuteNonQuery();
        }
        new VisualConceptRepository(_database).Reject(_first,"scene.mountain");
        new ClassificationProjectionService(_database).RestoreSuggestion(_duplicate,"scene.mountain");
        using(var learn=_database.Command("UPDATE images SET sha256=$sha WHERE id IN ($first,$second)"))
        {
            learn.Parameters.AddWithValue("$first",_first);learn.Parameters.AddWithValue("$second",_duplicate);
            learn.Parameters.AddWithValue("$sha",new string('a',64));learn.ExecuteNonQuery();
        }
        var conflict=Assert.Single(_database.GetMetadataEditConflicts());
        Assert.NotEqual(conflict.Proposed.Value,conflict.Current.Value);
        _database.ResolveMetadataEditConflict(conflict.OperationId,true,Math.Max(conflict.Current.ServerVersion,conflict.Current.BaseVersion),conflict.Current.OperationId);
        Assert.Empty(_database.GetMetadataEditConflicts());
        Assert.Equal(conflict.Proposed.Value,Assert.Single(_database.GetMetadataEdits()).Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyClassificationDecisionsAcquireContentBoundFieldsWithoutCrossingOwners(bool verifiedHash)
    {
        if(!verifiedHash)
        {
            using var clear=_database.Command("UPDATE images SET sha256=NULL WHERE id=$id");
            clear.Parameters.AddWithValue("$id",_first);clear.ExecuteNonQuery();
        }
        var repository=new VisualConceptRepository(_database);
        repository.Confirm(_first,"scene.beach");repository.Reject(_first,"scene.mountain");
        using(var clear=_database.Command("DELETE FROM metadata_edits;DELETE FROM metadata_edit_conflicts;DELETE FROM metadata_causal_receipts")) clear.ExecuteNonQuery();
        using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+_database.DatabasePath))
        {
            connection.Open();LegacySchemaFixture.RevertPhotoClassificationIdentity(connection);
            using var version=connection.CreateCommand();version.CommandText="PRAGMA user_version=73";version.ExecuteNonQuery();
        }
        var path=_database.DatabasePath;_database.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var migrated=new PicerDatabase(path);
        var fields=migrated.GetMetadataEdits();
        Assert.Contains(fields,edit=>edit.Kind=="photo-classification-confirmation" && edit.Field=="scene.beach" && edit.Value=="1");
        Assert.Contains(fields,edit=>edit.Kind=="photo-classification-policy" && edit.Field=="scene.mountain" && edit.Value=="rejected");
        Assert.DoesNotContain(fields,edit=>edit.EntityId==_foreign);
        Assert.All(fields,edit=>Assert.StartsWith(verifiedHash?"sha256:":"local:",edit.PhotoContentIdentity));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReplacingFileContentDoesNotCarryOldClassificationProjectionsIntoNewPhoto(bool verifiedHash)
    {
        if(!verifiedHash)
        {
            using var unknown=_database.Command("UPDATE images SET sha256=NULL WHERE id=$id");
            unknown.Parameters.AddWithValue("$id",_first);unknown.ExecuteNonQuery();
        }
        var repository=new VisualConceptRepository(_database);
        repository.Confirm(_first,"scene.beach");repository.Reject(_first,"scene.mountain");
        using(var replace=_database.Command("UPDATE images SET sha256=$sha,file_size=11 WHERE id=$id"))
        { replace.Parameters.AddWithValue("$sha",new string('c',64));replace.Parameters.AddWithValue("$id",_first);replace.ExecuteNonQuery(); }
        var confirmed=IndexSearchEngine.Search(_database,new SearchFilters { VisualConceptIds=["scene.beach"] });
        Assert.DoesNotContain(confirmed,image=>image.Id==_first);
        if(verifiedHash) Assert.Contains(confirmed,image=>image.Id==_duplicate);
        var projection=new ClassificationProjectionService(_database);var run=projection.BeginOrResume();
        var progress=projection.ProcessNextBatch(run.RunId,10,semanticResults:new[]{_first,_duplicate,_foreign}.ToDictionary(id=>id,
            _=>new SemanticAnalysisResult([new VisualConceptScore("scene.mountain",0.99f)])));
        Assert.Equal(0,progress.Failed);
        using var diagnostic=_database.Command("SELECT group_concat(image_id||':'||concept_id||':'||source_kind||':'||review_state||':'||COALESCE(content_sha256,'null'),' / ') FROM image_classification_assertions");
        Assert.True(IndexSearchEngine.Search(_database,new SearchFilters { VisualConceptIds=["scene.mountain"] }).Any(image=>image.Id==_first),diagnostic.ExecuteScalar() as string);
    }

    [Fact]
    public void TimestampOnlyChangeWithUnchangedVerifiedBytesKeepsManualClassification()
    {
        new VisualConceptRepository(_database).Confirm(_first,"scene.beach");
        using(var touch=_database.Command("UPDATE images SET modified_utc=$time WHERE id=$id"))
        { touch.Parameters.AddWithValue("$time",DateTime.UtcNow.ToString("O"));touch.Parameters.AddWithValue("$id",_first);touch.ExecuteNonQuery(); }
        Assert.Contains(IndexSearchEngine.Search(_database,new SearchFilters { VisualConceptIds=["scene.beach"] }),image=>image.Id==_first);
    }

    [Fact]
    public void LegacyStaleAssertionSeedsOnlyASurvivingCopyOfItsOriginalContent()
    {
        using(var seed=_database.Command("""
            UPDATE images SET sha256=$new,content_sha256=$new WHERE id=$id;
            INSERT INTO image_classification_assertions(image_id,content_sha256,concept_id,source_kind,source_version,
                assertion_state,review_state,created_utc,updated_utc,reviewed_utc)
            VALUES($id,$old,'scene.beach','user','user-v1','user_confirmed','accepted',$now,$now,$now);
            """))
        {
            seed.Parameters.AddWithValue("$id",_first);seed.Parameters.AddWithValue("$new",new string('c',64));
            seed.Parameters.AddWithValue("$old",new string('a',64));seed.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));seed.ExecuteNonQuery();
        }
        using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+_database.DatabasePath))
        {
            connection.Open();LegacySchemaFixture.RevertPhotoClassificationIdentity(connection);
            using var version=connection.CreateCommand();version.CommandText="PRAGMA user_version=73";version.ExecuteNonQuery();
        }
        var path=_database.DatabasePath;_database.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var migrated=new PicerDatabase(path);
        var field=Assert.Single(migrated.GetMetadataEdits());
        Assert.Equal(_duplicate,field.EntityId);Assert.Equal("sha256:"+new string('a',64),field.PhotoContentIdentity);
        Assert.DoesNotContain(IndexSearchEngine.Search(migrated,new SearchFilters { VisualConceptIds=["scene.beach"] }),image=>image.Id==_first);
        Assert.Equal(_duplicate,Assert.Single(IndexSearchEngine.Search(migrated,new SearchFilters { VisualConceptIds=["scene.beach"] })).Id);
    }

    private static MetadataEdit Policy(long id,string value,long baseline=0)=>
        new("photo-classification-policy",id,"scene.mountain",value,DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N")) { BaseVersion=baseline };

    private long Add(char owner,string name)
    {
        var root=PublishedMediaPath.Qualify(new string(owner,32),@"C:\Photos");var source=_database.AddSource(root);
        return _database.UpsertImage(new ImageRecord { SourceId=source.Id,Path=root+"\\"+name+".jpg",Sha256=new string('a',64),
            FileSize=10,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow }).Id;
    }

    public void Dispose()
    {
        _database.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(_root,true);
    }
}
