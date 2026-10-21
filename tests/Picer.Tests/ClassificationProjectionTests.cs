using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.SemanticSearch;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class ClassificationProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-classification-projection-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;

    public ClassificationProjectionTests()
    {
        Directory.CreateDirectory(_root);
        _database = new PicerDatabase(Path.Combine(_root, "index.db"));
    }

    [Fact]
    public void ProjectionIsIncrementalAndMetadataFactsStayOutOfContentClassification()
    {
        var source = _database.AddSource(_root);
        AddImage(source.Id, "landscape.jpg", 800, 400, "camera");
        AddImage(source.Id, "portrait.jpg", 300, 900, null);
        var service = new ClassificationProjectionService(_database);

        var run = service.BeginOrResume();
        var first = service.ProcessNextBatch(run.RunId, 1);
        Assert.Equal(1, first.Processed);
        service.Pause(run.RunId);
        var resumed = service.BeginOrResume();
        var second = service.ProcessNextBatch(resumed.RunId, 10);
        var completed = service.ProcessNextBatch(resumed.RunId, 10);

        Assert.Equal(2, second.Processed);
        Assert.Equal("completed", completed.Status);
        Assert.DoesNotContain(service.List("automatic"), item => item.ConceptId.StartsWith("metadata.", StringComparison.Ordinal));
        Assert.Equal(2L, Scalar("SELECT COUNT(DISTINCT image_id) FROM image_classification_assertions WHERE source_kind='metadata_rule' AND review_state='accepted'"));
    }

    [Fact]
    public void ContentProjectionStoresMultipleSemanticLabelsForOnePhoto()
    {
        var source = _database.AddSource(_root);
        var imageId = AddImage(source.Id, "family-car.jpg", 800, 600, null);
        var service = new ClassificationProjectionService(_database);
        var run = service.BeginOrResume();

        service.ProcessNextBatch(run.RunId, 10, semanticResults: new Dictionary<long, SemanticAnalysisResult>
        {
            [imageId] = new([
                new VisualConceptScore("subject.people", 0.91f),
                new VisualConceptScore("object.vehicle", 0.83f),
                new VisualConceptScore("scene.outdoor", 0.78f)])
        });

        var labels = service.List("automatic").Where(item => item.ImageId == imageId).ToArray();
        Assert.Equal(3, labels.Length);
        Assert.Contains(labels, item => item.ConceptId == "subject.people");
        Assert.Contains(labels, item => item.ConceptId == "object.vehicle");
        Assert.Contains(labels, item => item.ConceptId == "scene.outdoor");
    }

    [Fact]
    public void SemanticClusteringCreatesAStableMultiPhotoCategory()
    {
        var source = _database.AddSource(_root);
        var ids = Enumerable.Range(1, 3).Select(index => AddImage(source.Id, $"trip-{index}.jpg", 800, 600, null)).ToArray();
        var projection = new ClassificationProjectionService(_database);
        var run = projection.BeginOrResume();
        var results = ids.ToDictionary(id => id, id => new SemanticAnalysisResult(
            [new VisualConceptScore("scene.landscape", 0.9f), new VisualConceptScore("scene.outdoor", 0.85f)],
            Embedding: [1f, 0.01f * id], EmbeddingModelVersion: "clip-test"));
        projection.ProcessNextBatch(run.RunId, 10, semanticResults: results);

        var clustered = new ContentSemanticClusterService(_database).RebuildLatest(similarityThreshold: 0.9f);

        Assert.Equal(1, clustered.ClusterCount);
        Assert.Equal(3, clustered.ClusteredImages);
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM semantic_content_cluster_members"));
        Assert.Contains(projection.List("automatic"), item => item.ConceptId.StartsWith("cluster.semantic.", StringComparison.Ordinal));

        var originalKey = Text("SELECT cluster_key FROM semantic_content_cluster_members WHERE image_id=$id", ("$id", ids[0]));
        var added = AddImage(source.Id, "trip-4.jpg", 800, 600, null);
        AddSuggestion(added, "scene.landscape", new string('c', 64));
        AddSuggestion(added, "scene.outdoor", new string('c', 64));
        new ClipSemanticRepository(_database).Upsert(_database.GetImage(added)!, "clip-test", new float[] { 1f, 0.04f });
        new ContentSemanticClusterService(_database).RebuildLatest(similarityThreshold: 0.9f);

        Assert.Equal(originalKey, Text("SELECT cluster_key FROM semantic_content_cluster_members WHERE image_id=$id", ("$id", ids[0])));
        Assert.Equal(originalKey, Text("SELECT cluster_key FROM semantic_content_cluster_members WHERE image_id=$id", ("$id", added)));

        var settings = new ContentSemanticClusterService(_database).SaveSettings(automatic: false, manualThreshold: 0.75f);
        var preview = new ContentSemanticClusterService(_database).Preview(settings.EffectiveThreshold);
        Assert.False(settings.Automatic);
        Assert.Equal(0.75f, settings.EffectiveThreshold);
        Assert.True(preview.EstimatedClusters >= 1);
    }

    [Fact]
    public void CorrectionRejectsOldSuggestionAndConfirmsNewConceptForDuplicateReferences()
    {
        var source = _database.AddSource(_root);
        var hash = new string('b', 64);
        var first = AddImage(source.Id, "one.jpg", 800, 400, null, hash);
        AddImage(source.Id, "two.jpg", 800, 400, null, hash);
        AddSuggestion(first, "scene.mountain", hash);

        new VisualConceptRepository(_database).Correct(first, "scene.mountain", "scene.beach");

        Assert.Equal("rejected", Text("SELECT review_state FROM image_classification_assertions WHERE image_id=$id AND concept_id='scene.mountain'", ("$id", first)));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM image_classification_assertions WHERE concept_id='scene.beach' AND source_kind='user' AND review_state='accepted'"));
    }

    [Fact]
    public void ReciprocalRankFusionRewardsAgreementAndKeepsSourceRanks()
    {
        var hits = ReciprocalRankFusion.Fuse([
            ("FTS", new[] { new RankedCandidate(1, 1), new RankedCandidate(2, 2) }.AsEnumerable()),
            ("CLIP", new[] { new RankedCandidate(2, 1), new RankedCandidate(3, 2) }.AsEnumerable())
        ], 3, rankConstant: 10);

        Assert.Equal(2, hits[0].MediaId);
        Assert.Equal(2, hits[0].SourceRanks.Count);
        Assert.Equal(1, hits[0].SourceRanks["CLIP"]);
    }

    [Fact]
    public void ClipStreamingHonorsHardFilterAndReportsInvalidDimensions()
    {
        var source = _database.AddSource(_root);
        var first = AddImage(source.Id, "one.jpg", 1, 1, null);
        var second = AddImage(source.Id, "two.jpg", 1, 1, null);
        var repository = new ClipSemanticRepository(_database);
        repository.Upsert(_database.GetImage(first)!, "model", new float[] { 1, 0 });
        repository.Upsert(_database.GetImage(second)!, "model", new float[] { 1, 0, 0 });

        var result = repository.SearchStreaming(new float[] { 1, 0 }, 5, "model", new HashSet<long> { first, second });

        Assert.Single(result.Hits);
        Assert.Equal(first, result.Hits[0].Image.Id);
        Assert.Equal(2, result.Eligible);
        Assert.Equal(1, result.InvalidVectors);
    }

    private long AddImage(long sourceId, string name, int width, int height, string? camera, string? hash = null) =>
        _database.UpsertImage(new ImageRecord
        {
            SourceId = sourceId,
            Path = Path.Combine(_root, name),
            FileSize = 10,
            Width = width,
            Height = height,
            Camera = camera,
            Sha256 = hash,
            ContentSha256 = hash,
            ModifiedUtc = DateTime.UtcNow,
            IndexedUtc = DateTime.UtcNow,
            Status = ImageStatus.Active
        }).Id;

    private void AddSuggestion(long imageId, string concept, string hash)
    {
        using var command = _database.Command("""
            INSERT INTO image_classification_assertions(image_id,content_sha256,concept_id,source_kind,source_version,
                assertion_state,review_state,confidence,created_utc,updated_utc)
            VALUES($image,$hash,$concept,'vision_model','test','suggested','pending',0.9,$now,$now)
            """);
        command.Parameters.AddWithValue("$image", imageId);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$concept", concept);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql, params (string Name, object Value)[] args)
    {
        using var command = _database.Command(sql);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private string Text(string sql, params (string Name, object Value)[] args)
    {
        using var command = _database.Command(sql);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar()?.ToString() ?? "";
    }

    public void Dispose()
    {
        _database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
