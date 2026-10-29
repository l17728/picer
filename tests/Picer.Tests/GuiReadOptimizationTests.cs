using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class GuiReadOptimizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-gui-read-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly long _source;

    public GuiReadOptimizationTests()
    {
        _db = new PicerDatabase(Path.Combine(_root, "picer.db"));
        _source = _db.AddSource(_root).Id;
    }

    private ImageRecord AddImage(string relative, long? source = null) => _db.UpsertImage(new ImageRecord
    {
        SourceId = source ?? _source, Path = Path.Combine(_root, relative), FileSize = 1,
        ModifiedUtc = DateTime.UtcNow.Date, TakenUtc = DateTime.UtcNow.Date, IndexedUtc = DateTime.UtcNow,
        Status = ImageStatus.Active
    });

    [Fact]
    public void BatchImagesAndAlbumsPreserveMembershipWithoutDuplicateIds()
    {
        var first = AddImage("a.jpg"); var second = AddImage("b.jpg");
        var album = SemanticEngine.AddAlbum(_db, "fixture");
        SemanticEngine.AddToAlbum(_db, album.Id, first.Id);
        SemanticEngine.AddToAlbum(_db, album.Id, second.Id);
        var values = _db.ReadSnapshot(reader => (Images: reader.GetImagesByIds([first.Id, first.Id, long.MaxValue]),
            Albums: SemanticEngine.GetAlbumImageMap(reader)));
        Assert.Equal(first.Id, Assert.Single(values.Images).Id);
        Assert.Equal(new[] { first.Id, second.Id }, values.Albums[album.Id].Select(image => image.Id).Order());
    }

    [Fact]
    public void DirectoryFiltersRespectBoundariesSourceAndLiteralWildcards()
    {
        var direct = AddImage("100%_trip/a.jpg"); var child = AddImage("100%_trip/child/b.jpg");
        AddImage("100%_trip-other/c.jpg"); AddImage("100xxtrip/d.jpg");
        AddImage("100%_trip/other.jpg", _db.AddSource(Path.Combine(_root, "second")).Id);
        var directory = Path.Combine(_root, "100%_trip");
        _db.ReadSnapshot(reader =>
        {
            Assert.Equal(new[] { direct.Id, child.Id }, reader.GetImagesInPathPrefix(_source, directory).Select(image => image.Id).Order());
            Assert.Equal(direct.Id, Assert.Single(reader.GetImagesInPathPrefix(_source, directory, recursive: false)).Id);
            var filters = new SearchFilters { SourceId = _source, DirectoryPath = directory };
            Assert.Equal(2, IndexSearchEngine.CountMatches(reader, filters));
            Assert.Equal(2, IndexSearchEngine.SearchPaged(reader, filters).Items.Count);
            Assert.Equal(2, IndexSearchEngine.Search(reader, filters).Count);
            return 0;
        });
    }

    [Fact]
    public void FilteredVideoPagesKeepEveryResultAndDirectoryCounts()
    {
        var repository = new VideoRepository(_db);
        var directory = Path.Combine(_root, "trip"); Directory.CreateDirectory(directory);
        var ids = new List<long>();
        for (var index = 0; index < 5; index++)
        {
            var path = Path.Combine(directory, $"match-{index}.mp4"); File.WriteAllBytes(path, [(byte)index]);
            ids.Add(repository.Upsert(_source, path).Id);
        }
        var outside = Path.Combine(_root, "match-outside.mp4"); File.WriteAllBytes(outside, [99]); repository.Upsert(_source, outside);
        var actual = new List<long>(); string? cursor = null;
        do
        {
            var page = _db.ReadSnapshot(reader => GatewayBrowseQueries.Videos(reader, new(_source, directory, "match"), 2, cursor));
            Assert.Equal(5, page.TotalCount); actual.AddRange(page.Items.Select(video => video.Id)); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(ids, actual);
        var counts = _db.ReadSnapshot(reader => GatewayBrowseQueries.Directories(reader));
        Assert.Equal(5, Assert.Single(counts.Where(row => row.Path == directory.Replace('\\', '/'))).Videos);
    }

    [Fact]
    public void PreparedVideoRejectsChangedFileBeforeDatabaseCommit()
    {
        var path = Path.Combine(_root, "prepared.mp4"); File.WriteAllBytes(path, [1]);
        var prepared = VideoRepository.PrepareFile(path);
        File.WriteAllBytes(path, [1, 2]);
        Assert.Throws<IOException>(() => new VideoRepository(_db).UpsertPrepared(_source, prepared));
        Assert.Empty(new VideoRepository(_db).GetAll());
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void SceneInputCanBeComputedAfterSnapshotClosesAndRemainsConsistent()
    {
        var images = new[] { AddImage("a.jpg"), AddImage("b.jpg") };
        var service = new SceneGroupDescriptionService(_db, new CloudVisionRepository(_db));
        var plan = service.BuildPlan(_root, images);
        service.SaveAccepted(plan, new(plan.Fingerprint, "before", "summary", [], "test", "v1", "p1", "accepted"));
        var captured = _db.ReadSnapshot(reader => new SceneTimeClusterService(reader).Capture(images));
        service.UpdateAcceptedText(plan.Fingerprint, "after", "updated");
        Assert.Equal("before", Assert.Single(SceneTimeClusterService.Build(captured)).SceneName);
        Assert.Equal("after", Assert.Single(new SceneTimeClusterService(_db).Build(images)).SceneName);
    }

    private ContentClusterSettings SeedCluster()
    {
        var images = Enumerable.Range(0, 3).Select(index => AddImage($"cluster-{index}.jpg")).ToArray();
        var projection = new ClassificationProjectionService(_db);
        var run = projection.BeginOrResume();
        projection.ProcessNextBatch(run.RunId, 10, semanticResults: images.ToDictionary(image => image.Id, _ =>
            new SemanticAnalysisResult([new VisualConceptScore("scene.landscape", .9f)],
                Embedding: [1f, 0f], EmbeddingModelVersion: "test-model")));
        return new ContentSemanticClusterService(_db).SaveSettings(false, .8f, refreshRecommendation: false);
    }

    [Fact]
    public void DetachedClusterPreviewAndRebuildWorkWithReadOnlyRepository()
    {
        var settings = SeedCluster();
        var input = _db.ReadSnapshot(reader =>
        {
            var service = new ContentSemanticClusterService(reader);
            _ = new CloudVisionRepository(reader).GetLatestCompleted(_db.GetImages(_source).Select(image => image.Id));
            return (Preview: service.CapturePreview(.8f), Rebuild: service.CaptureRebuild(settings));
        });
        Assert.True(input.Preview().EstimatedClusters > 0);
        var result = input.Rebuild().Commit(_db);
        Assert.Equal(3, result.ClusteredImages);
    }

    [Fact]
    public void DetachedRebuildRejectsSameCountInputEditEvenWithoutTimestampChange()
    {
        var settings = SeedCluster();
        var compute = _db.ReadSnapshot(reader => new ContentSemanticClusterService(reader).CaptureRebuild(settings));
        var plan = compute();
        using (var update = _db.Command("UPDATE image_classification_assertions SET concept_id='scene.indoor' WHERE concept_id='scene.landscape'"))
            Assert.True(update.ExecuteNonQuery() > 0);
        Assert.Equal("cluster_input_changed", Assert.Throws<InvalidOperationException>(() => plan.Commit(_db)).Message);
    }

    [Fact]
    public void CachedRecommendationDoesNotRequireAnotherWrite()
    {
        SeedCluster();
        var recommendation = _db.ReadSnapshot(reader => new ContentSemanticClusterService(reader).CaptureRecommendation());
        Assert.True(new ContentSemanticClusterService(_db).SaveRecommendation(recommendation, recommendation.Calculate()));
        var cached = _db.ReadSnapshot(reader => new ContentSemanticClusterService(reader).CaptureRecommendation());
        Assert.False(cached.NeedsRefresh);
        Assert.Equal(recommendation.Calculate(), cached.Calculate());
    }

    public void Dispose() { _db.Dispose(); Directory.Delete(_root, true); }
}
