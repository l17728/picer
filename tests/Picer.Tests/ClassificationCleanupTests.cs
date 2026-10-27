using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class ClassificationCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-classification-cleanup-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    private readonly long _sourceId;

    public ClassificationCleanupTests()
    {
        _database = new PicerDatabase(Path.Combine(_root, "picer.db"));
        _sourceId = _database.AddSource(_root).Id;
    }

    [Fact]
    public void ScreenshotLabelsAreRetractedAndExistingDescriptionsAreReprojected()
    {
        var image = AddImage("photo.jpg");
        InsertScreenshotAssertion(image, "accepted", "automatic");
        new CloudVisionRepository(_database).Save(image, new Analyzer(), "none", "test",
            new CloudVisionResult("自然景色", ["风景"], [], [], [], null, "none"));

        var repaired = new ClassificationProjectionService(_database).RepairObsoleteScreenshotClassifications();

        Assert.Equal(1, repaired);
        Assert.False(VisualConceptCatalog.DisplayNames.ContainsKey("document.screenshot"));
        using (var old = _database.Command("SELECT review_state FROM image_classification_assertions WHERE image_id=$id AND concept_id='document.screenshot'"))
        {
            old.Parameters.AddWithValue("$id", image.Id);
            Assert.Equal("retracted", old.ExecuteScalar());
        }
        using (var legacy = _database.Command("SELECT COUNT(*) FROM image_visual_concepts WHERE image_id=$id AND concept_id='document.screenshot'"))
        {
            legacy.Parameters.AddWithValue("$id", image.Id);
            Assert.Equal(0L, legacy.ExecuteScalar());
        }
        using var replacement = _database.Command("""
            SELECT COUNT(*) FROM image_classification_assertions
            WHERE image_id=$id AND concept_id='scene.landscape' AND assertion_state='suggested' AND review_state='pending'
            """);
        replacement.Parameters.AddWithValue("$id", image.Id);
        Assert.Equal(1L, replacement.ExecuteScalar());
    }

    [Fact]
    public void ExplicitlyRejectedScreenshotLabelsStayRejected()
    {
        var image = AddImage("rejected.jpg");
        InsertScreenshotAssertion(image, "rejected", "suggested");

        Assert.Equal(0, new ClassificationProjectionService(_database).RepairObsoleteScreenshotClassifications());

        using var state = _database.Command("SELECT review_state FROM image_classification_assertions WHERE image_id=$id AND concept_id='document.screenshot'");
        state.Parameters.AddWithValue("$id", image.Id);
        Assert.Equal("rejected", state.ExecuteScalar());
    }

    private ImageRecord AddImage(string name) => _database.UpsertImage(new ImageRecord
    {
        SourceId = _sourceId,
        Path = Path.Combine(_root, name),
        Sha256 = new string('b', 64),
        FileSize = 10,
        ModifiedUtc = DateTime.UtcNow,
        IndexedUtc = DateTime.UtcNow
    });

    private void InsertScreenshotAssertion(ImageRecord image, string reviewState, string assertionState)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var command = _database.Command("""
            INSERT INTO image_classification_assertions(image_id,concept_id,source_kind,source_version,
                assertion_state,review_state,created_utc,updated_utc)
            VALUES($id,'document.screenshot','vision_model','old-model',$assertion,$review,$now,$now)
            """);
        command.Parameters.AddWithValue("$id", image.Id);
        command.Parameters.AddWithValue("$assertion", assertionState);
        command.Parameters.AddWithValue("$review", reviewState);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        if (reviewState == "accepted")
        {
            using var legacy = _database.Command("""
                INSERT INTO image_visual_concepts(image_id,concept_id,model_version,confidence,state,created_utc)
                VALUES($id,'document.screenshot','old-model',0.9,'accepted',$now)
                """);
            legacy.Parameters.AddWithValue("$id", image.Id);
            legacy.Parameters.AddWithValue("$now", now);
            legacy.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Analyzer : ICloudVisionAnalyzer
    {
        public string Provider => "test";
        public string ModelVersion => "test";
        public string PromptVersion => "test";
        public Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
