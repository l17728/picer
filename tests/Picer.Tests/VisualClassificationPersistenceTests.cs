using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class VisualClassificationPersistenceTests : IDisposable
{
    [Fact]
    public void RemoteMirrorPathsKeepPhotoDecisionsInsideTheirPublisherWithoutCopyingBindings()
    {
        var hash = new string('c', 64);
        foreach (var (id, publisher) in new[] { (101L, 'a'), (102L, 'a'), (201L, 'b') })
        {
            var root = Picer.Core.Identity.PublishedMediaPath.Qualify(new string(publisher, 32), @"C:\Photos");
            var source = _database.AddSource(root);
            _database.UpsertMirroredImage(new ImageRecord
            {
                Id = id, SourceId = source.Id, Path = root + "\\" + id + ".jpg", FileSize = 10,
                Sha256 = hash, ContentSha256 = hash, ModifiedUtc = DateTime.UnixEpoch, IndexedUtc = DateTime.UtcNow
            });
            AddModelAssertion(id, "scene.mountain");
        }
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM sync_source_bindings"));
        var repository = new VisualConceptRepository(_database);
        repository.Reject(101, "scene.mountain");
        var remaining = IndexSearchEngine.Search(_database, new SearchFilters { VisualConceptIds = ["scene.mountain"], IncludeSuggestedClassifications = true });
        Assert.Equal(201, Assert.Single(remaining).Id);
        Assert.Equal("publisher:" + new string('a', 32), ScalarText(_database, "SELECT owner_key FROM image_classification_rejections WHERE content_sha256=$sha", ("$sha", hash)));
        repository.Confirm(101, "scene.beach");
        Assert.Equal(new long[] { 101, 102 }, IndexSearchEngine.Search(_database,
            new SearchFilters { VisualConceptIds = ["scene.beach"] }).Select(image => image.Id).Order().ToArray());
        new ImageRecordPurger(_database).Purge(101, requireMissing: false);
        new ImageRecordPurger(_database).Purge(102, requireMissing: false);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM image_classification_rejections WHERE content_sha256=$sha", ("$sha", hash)));
        Assert.NotNull(_database.GetImage(201));
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-classification-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    private readonly long _first;
    private readonly long _duplicate;
    private bool _databaseDisposed;

    public VisualClassificationPersistenceTests()
    {
        Directory.CreateDirectory(_root);
        _database = new PicerDatabase(Path.Combine(_root, "index.db"));
        var source = _database.AddSource(_root);
        var hash = new string('a', 64);
        _first = AddImage(source.Id, "one.jpg", hash);
        _duplicate = AddImage(source.Id, "two.jpg", hash);
    }

    [Fact]
    public void ConfirmationCreatesUserAssertionsWithoutChangingModelProvenance()
    {
        AddModelAssertion(_first, "scene.mountain");
        var repository = new VisualConceptRepository(_database);

        repository.Confirm(_first, "scene.mountain");
        repository.RemoveMachineClassification(_first, "scene.mountain");

        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM image_classification_assertions WHERE concept_id='scene.mountain' AND source_kind='user' AND assertion_state='user_confirmed'"));
        Assert.Equal("retracted", ScalarText($"SELECT review_state FROM image_classification_assertions WHERE image_id={_first} AND source_kind='vision_model' AND concept_id='scene.mountain'"));
        var results = IndexSearchEngine.Search(_database, new SearchFilters { VisualConceptIds = ["scene.mountain"] });
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void RejectingMachineClassificationDoesNotHideIndependentHumanConfirmationFromSearch()
    {
        AddModelAssertion(_first,"scene.mountain");
        var repository=new VisualConceptRepository(_database);
        repository.Confirm(_first,"scene.mountain");repository.Reject(_first,"scene.mountain");
        Assert.Equal(new[]{_first,_duplicate}.Order(),IndexSearchEngine.Search(_database,
            new SearchFilters { VisualConceptIds=["scene.mountain"] }).Select(image=>image.Id).Order());
        Assert.Empty(repository.GetScores(_first));
    }

    [Fact]
    public void RestoringRejectedAutomaticClassificationReturnsSuggestionsInsteadOfAutomaticSearchResults()
    {
        AddModelAssertion(_first,"scene.mountain");AddModelAssertion(_duplicate,"scene.mountain");
        using(var automatic=_database.Command("UPDATE image_classification_assertions SET assertion_state='automatic',review_state='accepted' WHERE concept_id='scene.mountain'"))
            automatic.ExecuteNonQuery();
        new VisualConceptRepository(_database).Reject(_first,"scene.mountain");
        new ClassificationProjectionService(_database).RestoreSuggestion(_first,"scene.mountain");
        Assert.Empty(IndexSearchEngine.Search(_database,new SearchFilters { VisualConceptIds=["scene.mountain"] }));
        Assert.Equal(2,IndexSearchEngine.Search(_database,new SearchFilters { VisualConceptIds=["scene.mountain"],IncludeSuggestedClassifications=true }).Count);
        Assert.Equal(2,new ClassificationProjectionService(_database).List("suggestions").Count);
    }

    [Fact]
    public void ExplicitPhotoPurgeForgetsItsInterruptedFaceImportButKeepsAnotherPhotosImport()
    {
        using (var seed = _database.Command("""
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
            VALUES('publisher','library','device','grant','space','publishing','epoch');
            INSERT INTO detected_faces(id,image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc)
            VALUES(30,$first,'model',0,0,0,10,10,0.9,'2026-10-23'),(40,$duplicate,'model',0,0,0,10,10,0.9,'2026-10-23');
            INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id) VALUES
                ('publisher','image',CAST($first AS TEXT),'image'),('publisher','face','30','face-one'),('publisher','face','40','face-two');
            INSERT INTO sync_pending_identity_adoptions(publisher_id,offer_id,kind,local_id,global_id) VALUES
                ('publisher','offer-one','face',30,'face-one'),('publisher','offer-two','face',40,'face-two');
            """))
        {
            seed.Parameters.AddWithValue("$first", _first); seed.Parameters.AddWithValue("$duplicate", _duplicate);
            seed.ExecuteNonQuery();
        }
        Assert.True(new ImageRecordPurger(_database).Purge(_first, requireMissing: false));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sync_pending_identity_adoptions"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sync_entity_identities"));
        Assert.Equal("face-two", ScalarText("SELECT global_id FROM sync_entity_identities"));
        Assert.True(new ImageRecordPurger(_database).Purge(_duplicate, requireMissing: false));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM sync_pending_identity_adoptions"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM sync_entity_identities"));
    }

    [Fact]
    public void HashDecisionsDoNotCrossPublisherOwnershipAndPurgeKeepsOtherOwnersDecision()
    {
        var foreign = AddForeignCopy();
        AddModelAssertion(_first, "scene.mountain");
        AddModelAssertion(_duplicate, "scene.mountain");
        AddModelAssertion(foreign, "scene.mountain");
        var repository = new VisualConceptRepository(_database);
        repository.Reject(_first, "scene.mountain");
        var filters = new SearchFilters { VisualConceptIds = ["scene.mountain"], IncludeSuggestedClassifications = true };
        Assert.Equal(foreign, Assert.Single(IndexSearchEngine.Search(_database, filters)).Id);
        repository.Reject(foreign, "scene.mountain");
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM image_classification_rejections WHERE content_sha256 IS NOT NULL"));
        new ClassificationProjectionService(_database).RestoreSuggestion(_first, "scene.mountain");
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM image_classification_rejections WHERE content_sha256 IS NOT NULL"));
        Assert.Equal(new[]{_first,_duplicate}.Order(),IndexSearchEngine.Search(_database,filters).Select(image=>image.Id).Order());
        Assert.Contains(repository.GetScores(_duplicate),score=>score.ConceptId=="scene.mountain");
        Assert.Equal(0L,Scalar("SELECT COUNT(*) FROM visual_concept_rejections WHERE image_id IN ($first,$duplicate)",("$first",_first),("$duplicate",_duplicate)));
        repository.Correct(_first, "scene.mountain", "scene.beach");
        Assert.Equal(2, IndexSearchEngine.Search(_database, new SearchFilters { VisualConceptIds = ["scene.beach"] }).Count);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM image_classification_assertions WHERE image_id=$id AND concept_id='scene.beach'", ("$id", foreign)));
        new ImageRecordPurger(_database).Purge(_first, requireMissing: false);
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM image_classification_rejections WHERE content_sha256 IS NOT NULL"));
        new ImageRecordPurger(_database).Purge(_duplicate, requireMissing: false);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM image_classification_rejections WHERE content_sha256 IS NOT NULL"));
        new ImageRecordPurger(_database).Purge(foreign, requireMissing: false);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM image_classification_rejections"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM sync_metadata_row_journal WHERE table_name='image_classification_rejections'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitConfirmationOrCorrectionOverridesAnOldRejectionOnlyForItsOwner(bool correction)
    {
        var foreign = AddForeignCopy();
        var repository = new VisualConceptRepository(_database);
        repository.Reject(_first, "scene.beach");
        repository.Reject(foreign, "scene.beach");
        if (correction) repository.Correct(_first, "scene.mountain", "scene.beach");
        else repository.Confirm(_first, "scene.beach");
        var results = IndexSearchEngine.Search(_database, new SearchFilters { VisualConceptIds = ["scene.beach"] });
        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, image => image.Id == foreign);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM image_classification_rejections WHERE concept_id='scene.beach'"));
    }

    [Fact]
    public void RestoringUnhashedPhotoDoesNotRestoreAnotherReferenceOrRemoveHumanConfirmation()
    {
        var source=_database.AddSource(Path.Combine(_root,"unhashed"));
        var first=AddImage(source.Id,"unhashed-first.jpg",null);
        var second=AddImage(source.Id,"unhashed-second.jpg",null);
        AddModelAssertion(first,"scene.mountain");AddModelAssertion(second,"scene.mountain");
        var repository=new VisualConceptRepository(_database);
        repository.Confirm(first,"scene.mountain");
        repository.Reject(first,"scene.mountain");repository.Reject(second,"scene.mountain");
        new ClassificationProjectionService(_database).RestoreSuggestion(first,"scene.mountain");
        Assert.Equal(first,Assert.Single(IndexSearchEngine.Search(_database,new SearchFilters { VisualConceptIds=["scene.mountain"] })).Id);
        Assert.Equal(1L,Scalar("SELECT COUNT(*) FROM image_classification_assertions WHERE image_id=$id AND source_kind='user' AND review_state='accepted'",("$id",first)));
        Assert.Equal(1L,Scalar("SELECT COUNT(*) FROM image_classification_assertions WHERE image_id=$id AND source_kind='vision_model' AND review_state='rejected'",("$id",second)));
        Assert.Equal(1L,Scalar("SELECT COUNT(*) FROM visual_concept_rejections WHERE image_id=$id",("$id",second)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelReprojectionRespectsOwnedHashRejectionWithoutSuppressingOtherPublisher(bool foreignRejects)
    {
        var foreign = AddForeignCopy();
        new VisualConceptRepository(_database).Reject(foreignRejects ? foreign : _first, "scene.mountain");
        var projection = new ClassificationProjectionService(_database);
        var run = projection.BeginOrResume();
        projection.ProcessNextBatch(run.RunId, 10, semanticResults: new[] { _first, _duplicate, foreign }
            .ToDictionary(id => id, _ => new SemanticAnalysisResult([new VisualConceptScore("scene.mountain", 0.99f)])));
        var results = IndexSearchEngine.Search(_database, new SearchFilters { VisualConceptIds = ["scene.mountain"] });
        Assert.Equal(foreignRejects ? 2 : 1, results.Count);
        if (foreignRejects) Assert.DoesNotContain(results, image => image.Id == foreign);
        else Assert.Equal(foreign, Assert.Single(results).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyHashRejectionIsAdoptedOnlyWhenOwnershipIsUnambiguous(bool multipleOwners)
    {
        if (multipleOwners) AddForeignCopy();
        using (var legacy = _database.Command("INSERT INTO image_classification_rejections(content_sha256,concept_id,source_scope,rejected_utc) VALUES($sha,'scene.mountain','vision_model',$now)"))
        {
            legacy.Parameters.AddWithValue("$sha", new string('a', 64));
            legacy.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            legacy.ExecuteNonQuery();
        }
        _database.Dispose(); _databaseDisposed = true;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_root, "index.db")};Pooling=False"))
        {
            connection.Open();
            LegacySchemaFixture.RevertVideoMetadataOwnership(connection);
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "PRAGMA user_version=59"; downgrade.ExecuteNonQuery();
        }
        using var migrated = new PicerDatabase(Path.Combine(_root, "index.db"));
        var owner = ScalarText(migrated, "SELECT owner_key FROM image_classification_rejections");
        if (multipleOwners) Assert.Equal("", owner);
        else Assert.StartsWith("library:", owner);
        Assert.Equal(1L, Scalar(migrated, "SELECT COUNT(*) FROM image_classification_rejections"));
        var projection = new ClassificationProjectionService(migrated);
        if (multipleOwners)
        {
            Assert.Equal(2, projection.List("ownership").Count);
            new VisualConceptRepository(migrated).Reject(_first, "scene.mountain");
            var remaining = Assert.Single(projection.List("ownership"));
            Assert.NotEqual(_first, remaining.ImageId);
            projection.RestoreSuggestion(remaining.ImageId, remaining.ConceptId);
            Assert.Empty(projection.List("ownership"));
            Assert.Equal(2L, Scalar(migrated, "SELECT COUNT(*) FROM classification_rejection_ownership_reviews"));
            foreach (var image in migrated.GetImages(includeMissing: true).ToArray())
                new ImageRecordPurger(migrated).Purge(image.Id, requireMissing: false);
            Assert.Equal(0L, Scalar(migrated, "SELECT COUNT(*) FROM classification_rejection_ownership_reviews"));
            Assert.Equal(0L, Scalar(migrated, "SELECT COUNT(*) FROM image_classification_rejections"));
        }
        else Assert.Empty(projection.List("ownership"));
    }

    private long AddForeignCopy()
    {
        var source = _database.AddSource(Path.Combine(_root, "foreign"));
        using var bind = _database.Command("""
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
            VALUES('foreign-publisher','foreign-library','foreign-device','foreign-grant','space','receiving','epoch');
            INSERT INTO sync_source_bindings(publisher_id,local_source_id,source_id,source_uuid,original_path)
            VALUES('foreign-publisher',1,$source,'foreign-source','C:\\Photos');
            """);
        bind.Parameters.AddWithValue("$source", source.Id); bind.ExecuteNonQuery();
        return AddImage(source.Id, "foreign.jpg", new string('a', 64));
    }

    [Fact]
    public void RejectionUsesVerifiedContentHashAcrossDuplicateReferences()
    {
        AddModelAssertion(_first, "scene.mountain");
        AddModelAssertion(_duplicate, "scene.mountain");
        new VisualConceptRepository(_database).Reject(_first, "scene.mountain");

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM image_classification_rejections WHERE content_sha256=$sha AND concept_id='scene.mountain'",
            ("$sha", new string('a', 64))));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM image_classification_assertions WHERE concept_id='scene.mountain' AND review_state='rejected'"));
        Assert.Empty(IndexSearchEngine.Search(_database, new SearchFilters
        {
            VisualConceptIds = ["scene.mountain"], IncludeSuggestedClassifications = true
        }));
    }

    [Fact]
    public void SuggestedClassificationsAreExcludedUnlessExplicitlyRequested()
    {
        AddModelAssertion(_first, "scene.mountain");
        Assert.Empty(IndexSearchEngine.Search(_database, new SearchFilters { VisualConceptIds = ["scene.mountain"] }));
        Assert.Single(IndexSearchEngine.Search(_database, new SearchFilters
        {
            VisualConceptIds = ["scene.mountain"], IncludeSuggestedClassifications = true
        }));
    }

    [Fact]
    public void MigrationPreservesSuggestionsLegacyEffectiveRowsAndOldRejections()
    {
        var now = DateTime.UtcNow.ToString("o");
        using (var insert = _database.Command("""
            INSERT INTO image_visual_concepts(image_id,concept_id,model_version,confidence,state,created_utc)
            VALUES($image,'scene.mountain','old-model',0.8,'suggested',$now),
                  ($duplicate,'scene.beach','old-model',0.7,'accepted',$now);
            INSERT INTO visual_concept_rejections(image_id,concept_id,rejected_utc)
            VALUES($image,'scene.snow',$now);
            """))
        {
            insert.Parameters.AddWithValue("$image", _first);
            insert.Parameters.AddWithValue("$duplicate", _duplicate);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }
        _database.Dispose();
        _databaseDisposed = true;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_root, "index.db")};Pooling=False"))
        {
            connection.Open();
            using var downgradeFixture = connection.CreateCommand();
            LegacySchemaFixture.RevertVideoMetadataOwnership(connection);
            downgradeFixture.CommandText = "PRAGMA user_version=46";
            downgradeFixture.ExecuteNonQuery();
        }

        using var migrated = new PicerDatabase(Path.Combine(_root, "index.db"));
        Assert.Equal("suggested", ScalarText(migrated, "SELECT assertion_state FROM image_classification_assertions WHERE image_id=$id AND concept_id='scene.mountain'", ("$id", _first)));
        Assert.Equal("legacy_effective", ScalarText(migrated, "SELECT assertion_state FROM image_classification_assertions WHERE image_id=$id AND concept_id='scene.beach'", ("$id", _duplicate)));
        Assert.Equal(1L, Scalar(migrated, "SELECT COUNT(*) FROM image_classification_rejections WHERE image_id=$id AND concept_id='scene.snow'", ("$id", _first)));
    }

    private long AddImage(long sourceId, string name, string? sha) => _database.UpsertImage(new ImageRecord
    {
        SourceId = sourceId, Path = Path.Combine(_root, name), FileSize = 10,
        Sha256 = sha, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active
    }).Id;

    private void AddModelAssertion(long imageId, string concept)
    {
        using var command = _database.Command("""
            INSERT INTO image_classification_assertions(
                image_id,content_sha256,concept_id,source_kind,source_version,assertion_state,review_state,confidence,created_utc,updated_utc)
            SELECT id,lower(sha256),$concept,'vision_model','test-model','suggested','pending',0.9,$now,$now
            FROM images WHERE id=$image
            """);
        command.Parameters.AddWithValue("$concept", concept);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("$image", imageId);
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql, params (string Name, object Value)[] args)
        => Scalar(_database, sql, args);

    private static long Scalar(PicerDatabase database, string sql, params (string Name, object Value)[] args)
    {
        using var command = database.Command(sql);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private string ScalarText(string sql)
    {
        using var command = _database.Command(sql);
        return command.ExecuteScalar()?.ToString() ?? "";
    }

    private static string ScalarText(PicerDatabase database, string sql, params (string Name, object Value)[] args)
    {
        using var command = database.Command(sql);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar()?.ToString() ?? "";
    }

    public void Dispose()
    {
        if (!_databaseDisposed) _database.Dispose();
        SqliteConnectionPoolClear();
        Directory.Delete(_root, true);
    }

    private static void SqliteConnectionPoolClear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}
