using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class VisualClassificationPersistenceTests : IDisposable
{
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
