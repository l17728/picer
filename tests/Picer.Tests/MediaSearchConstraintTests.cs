using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class MediaSearchConstraintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-search-constraints-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly long _imageId;

    public MediaSearchConstraintTests()
    {
        Directory.CreateDirectory(_root);
        _db = new PicerDatabase(Path.Combine(_root, "index.db"));
        var source = _db.AddSource(_root);
        _imageId = _db.UpsertImage(new ImageRecord
        {
            SourceId = source.Id, Path = Path.Combine(_root, "mountain.jpg"), FileSize = 10,
            ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active
        }).Id;
        using var insert = _db.Command("""
            INSERT INTO image_classification_assertions(
                image_id,concept_id,source_kind,source_version,assertion_state,review_state,confidence,created_utc,updated_utc)
            VALUES($image,'landscape.mountain','vision_model','test','suggested','pending',0.9,$utc,$utc)
            """);
        insert.Parameters.AddWithValue("$image", _imageId);
        insert.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("o"));
        insert.ExecuteNonQuery();
    }

    [Fact]
    public void ConceptConstraintSurvivesTextAndLocationRelaxation()
    {
        var tool = new MediaSearchTool(_db);
        var request = new MediaSearchRequest(VisualConceptIds: ["landscape.mountain"],
            Terms: ["mountain"], Location: GeoPlaceResolver.Resolve("西安"), IncludeSuggestedClassifications: true);
        Assert.Equal(_imageId, Assert.Single(tool.Execute(request).Items).Id);
        Assert.Empty(tool.Execute(request with { AutoRelax = false }).Items);
        Assert.Empty(tool.Execute(request with { VisualConceptIds = ["landscape.sea"] }).Items);
        new VisualConceptRepository(_db).Reject(_imageId, "landscape.mountain");
        Assert.Empty(tool.Execute(request).Items);
    }

    [Fact]
    public void AllConceptsMustMatchAndEmptyConceptIsRejected()
    {
        var tool = new MediaSearchTool(_db);
        Assert.Empty(tool.Execute(new(VisualConceptIds: ["landscape.mountain", "landscape.sea"])).Items);
        Assert.Throws<ArgumentException>(() => tool.Execute(new(VisualConceptIds: [" "])));
    }

    [Fact]
    public void VideoOnlyCandidateCountDoesNotIncludePhotos()
    {
        var result = new MediaSearchTool(_db, new VideoRepository(_db)).Execute(
            new(MediaType: MediaTypeFilter.Video, VisualConceptIds: ["landscape.mountain"]));
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCandidates);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("视频暂不支持"));
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
