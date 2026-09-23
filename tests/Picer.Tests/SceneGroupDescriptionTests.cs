using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class SceneGroupDescriptionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"picer-scene-group-{Guid.NewGuid():N}");
    private readonly PicerDatabase _db;

    public SceneGroupDescriptionTests()
    {
        Directory.CreateDirectory(_root);
        _db = new PicerDatabase(Path.Combine(_root, "picer.db"));
    }

    [Fact]
    public void FineClusters_SampleFrontMiddleBackSummarizePersistAndEnterSearch()
    {
        var source = _db.AddSource(_root);
        var start = new DateTime(2026, 8, 29, 8, 0, 0, DateTimeKind.Utc);
        var images = Enumerable.Range(0, 9).Select(index =>
        {
            var path = Path.Combine(_root, $"photo-{index}.jpg"); File.WriteAllBytes(path, [(byte)index]);
            return _db.UpsertImage(new ImageRecord
            {
                SourceId = source.Id, Path = path, FileSize = 1, ModifiedUtc = start.AddMinutes(index * 10),
                TakenUtc = start.AddMinutes(index * 10), IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active
            });
        }).ToArray();
        var cloud = new CloudVisionRepository(_db); var analyzer = new ScenarioAnalyzer();
        var service = new SceneGroupDescriptionService(_db, cloud);
        var plan = service.BuildPlan(_root, images, maximumSubgroups: 5, maximumRepresentatives: 3);

        Assert.Equal(5, plan.Subgroups.Count);
        Assert.Equal([0, 2, 4], SceneGroupDescriptionService.EvenlySpacedIndices(5, 3));
        Assert.Equal(plan.Subgroups[0][0].Id, plan.Representatives[0].Id);
        Assert.Equal(plan.Subgroups[2][0].Id, plan.Representatives[1].Id);
        Assert.Equal(plan.Subgroups[4][0].Id, plan.Representatives[2].Id);

        foreach (var image in plan.Representatives)
            cloud.Save(image, analyzer, "location-none", "scene-representative",
                new CloudVisionResult("海边散步并观看日落", ["海边"], ["散步"], ["海面"], ["日落"]));
        var summary = service.Summarize(plan);
        Assert.NotNull(summary);
        Assert.Contains("海边", summary!.SceneName);
        service.SaveAccepted(plan, summary);

        Assert.Equal(summary.SceneName, service.GetAccepted(plan.Fingerprint)!.SceneName);
        var cluster = Assert.Single(new SceneTimeClusterService(_db).Build(images, TimeSpan.FromHours(3)));
        Assert.Equal(summary.SceneName, cluster.SceneName);
        Assert.Equal(images.Length, IndexSearchEngine.Search(_db,
            new SearchFilters { Query = "日落", IncludeMissing = false }).Count);

        service.UpdateAcceptedText(plan.Fingerprint, "海滨毕业旅行", "同学们在海边庆祝毕业");
        var edited = service.GetAccepted(plan.Fingerprint)!;
        Assert.Equal("海滨毕业旅行", edited.SceneName);
        Assert.Equal("同学们在海边庆祝毕业", edited.Summary);
        Assert.Equal(images.Length, IndexSearchEngine.Search(_db,
            new SearchFilters { Query = "毕业", IncludeMissing = false }).Count);
    }

    [Fact]
    public void AcceptedDescription_FallsBackToStableDirectoryAndTimeAcrossMirrorIds()
    {
        var source = _db.AddSource(_root);
        var start = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var images = Enumerable.Range(0, 2).Select(index =>
        {
            var path = Path.Combine(_root, $"mirror-{index}.jpg"); File.WriteAllBytes(path, [1]);
            return _db.UpsertImage(new ImageRecord
            {
                SourceId = source.Id, Path = path, FileSize = 1,
                ModifiedUtc = start.AddMinutes(index), TakenUtc = start.AddMinutes(index),
                IndexedUtc = start, Status = ImageStatus.Active
            });
        }).ToArray();
        var service = new SceneGroupDescriptionService(_db, new CloudVisionRepository(_db));
        var plan = service.BuildPlan(_root, images);
        service.SaveAccepted(plan, new SceneGroupDescription(plan.Fingerprint, "远程聚餐", "远程摘要", ["聚餐"], "test", "v1", "p1", "accepted"));

        var mirrorRows = images.Select((image, index) => new ImageRecord
        {
            Id = image.Id + 10000, SourceId = image.SourceId, Path = image.Path,
            FileSize = image.FileSize, ModifiedUtc = image.ModifiedUtc, TakenUtc = image.TakenUtc,
            IndexedUtc = image.IndexedUtc, Status = ImageStatus.Active
        }).ToArray();
        var resolved = service.GetAcceptedForGroup(_root, mirrorRows);
        Assert.NotNull(resolved);
        Assert.Equal("远程聚餐", resolved!.SceneName);
        Assert.Equal("远程摘要", resolved.Summary);
    }

    public void Dispose() { _db.Dispose(); try { Directory.Delete(_root, true); } catch { } }

    private sealed class ScenarioAnalyzer : ICloudVisionAnalyzer
    {
        public string Provider => "scenario";
        public string ModelVersion => "scene-v1";
        public string PromptVersion => "scene-prompt-v1";
        public Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
