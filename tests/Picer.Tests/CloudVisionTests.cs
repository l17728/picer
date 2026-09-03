using Microsoft.Data.Sqlite;
using System.Net;
using System.Text;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Geocoding;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Vision;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Picer.Tests;

public sealed class CloudVisionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-cloud-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    private readonly ImageRecord _image;

    public CloudVisionTests()
    {
        Directory.CreateDirectory(_root);
        _database = new PicerDatabase(Path.Combine(_root, "cloud.db"));
        var source = _database.AddSource(_root);
        var path = Path.Combine(_root, "photo.jpg");
        File.WriteAllBytes(path, [1, 2, 3]);
        _image = _database.UpsertImage(new ImageRecord
        {
            SourceId = source.Id, Path = path, FileSize = 3,
            ModifiedUtc = File.GetLastWriteTimeUtc(path), Gps = "33.159,103.881",
            Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow
        });
    }

    [Fact]
    public void Database_MigratesCloudVisionSchemaToVersionEight()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "cloud.db")}");
        connection.Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(25L, (long)version.ExecuteScalar()!);
        using var table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cloud_vision_analyses'";
        Assert.Equal(1L, (long)table.ExecuteScalar()!);
    }

    [Fact]
    public async Task CloudAnalysis_UsesGpsContextAndCachesCompletedResult()
    {
        var analyzer = new FakeCloudAnalyzer();
        var repository = new CloudVisionRepository(_database);
        var service = new CloudVisionAnalysisService(repository, analyzer, new FakeGeocoder());
        var candidates = new[] { (_image, (IReadOnlyList<VisualConceptScore>)[new("scene.mountain", .8f)], "complex-scene") };

        var first = await service.AnalyzeAsync(candidates);
        var second = await service.AnalyzeAsync(candidates);

        Assert.Equal(1, first.Completed);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal("九寨沟风景名胜区", analyzer.LastRequest!.Location!.Poi);
        Assert.Null(analyzer.LastRequest.Location.Latitude);
        Assert.Equal("scene.mountain", Assert.Single(analyzer.LastRequest.LocalConcepts).ConceptId);
        var stored = repository.Get(_image.Id, analyzer.Provider);
        Assert.Equal("completed", stored?.Status);
        Assert.Contains("九寨沟", stored?.Summary);
        var task = Assert.Single(repository.GetRecentTasks());
        Assert.Equal("completed", task.Status);
        Assert.Contains("九寨沟", repository.GetSuggestedTags(_image.Id));
        Assert.Equal(_image.Id, Assert.Single(IndexSearchEngine.Search(_database,
            new SearchFilters { Query = "九寨沟", IncludeMissing = false })).Id);
    }

    [Fact]
    public void RoutingPolicy_OnlyEscalatesComplexOrUncertainPhotos()
    {
        var policy = new CloudVisionRoutingPolicy();
        Assert.False(policy.Evaluate([new("scene.mountain", .91f)], 1).ShouldAnalyze);
        Assert.Equal("multiple-people", policy.Evaluate([new("scene.mountain", .91f)], 3).Reason);
        Assert.Equal("low-local-confidence", policy.Evaluate([new("scene.mountain", .4f)], 1).Reason);
        Assert.Equal("user-requested", policy.Evaluate([], 0, userRequested: true).Reason);
        Assert.Equal(0, new CloudVisionRunPolicy().ClampCount(50));
        Assert.Equal(10, new CloudVisionRunPolicy(true, 10).ClampCount(50));
    }

    [Fact]
    public void SceneTimeClusters_DefaultToDirectPhotosAndOnlyIncludeChildrenWhenRequested()
    {
        _image.CreatedUtc=DateTime.UtcNow.AddHours(-2);_database.UpdateImage(_image);
        var child=Path.Combine(_root,"child");Directory.CreateDirectory(child);var childPath=Path.Combine(child,"nested.jpg");File.WriteAllBytes(childPath,[4,5,6]);
        var nested=_database.UpsertImage(new ImageRecord{SourceId=_image.SourceId,Path=childPath,FileSize=3,ModifiedUtc=File.GetLastWriteTimeUtc(childPath),CreatedUtc=DateTime.UtcNow.AddHours(-1),Status=ImageStatus.Active,IndexedUtc=DateTime.UtcNow});
        var service=new SceneTimeClusterService(_database);
        Assert.Equal([_image.Id],Assert.Single(service.Build(_root,includeDescendants:false)).ImageIds);
        Assert.Equal(2,service.Build(_root,includeDescendants:true).Sum(cluster=>cluster.ImageIds.Count));
        Assert.Empty(service.Build(child+"-empty",includeDescendants:false));
    }

    [Fact]
    public void SceneTimeClusters_CanGroupAnExplicitPersonPhotoSetAcrossDirectories()
    {
        _image.CreatedUtc = DateTime.UtcNow.AddHours(-8);
        _database.UpdateImage(_image);
        var otherDirectory = Path.Combine(_root, "person-album");
        Directory.CreateDirectory(otherDirectory);
        var otherPath = Path.Combine(otherDirectory, "recent.jpg");
        File.WriteAllBytes(otherPath, [7, 8, 9]);
        var recent = _database.UpsertImage(new ImageRecord
        {
            SourceId = _image.SourceId, Path = otherPath, FileSize = 3,
            ModifiedUtc = File.GetLastWriteTimeUtc(otherPath), CreatedUtc = DateTime.UtcNow,
            Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow
        });

        var clusters = new SceneTimeClusterService(_database).Build([recent, _image]);

        Assert.Equal(2, clusters.Count);
        Assert.Equal([_image.Id, recent.Id], clusters.SelectMany(cluster => cluster.ImageIds));
    }

    [Fact]
    public void SceneTimeClusters_KeepPartiallyAnalyzedGroupsUnclassifiedAndPreserveAcceptedGroup()
    {
        var start = DateTime.UtcNow.AddHours(-2);
        _image.CreatedUtc = start;
        _database.UpdateImage(_image);
        var images = new List<ImageRecord> { _image };
        for (var index = 1; index <= 2; index++)
        {
            var path = Path.Combine(_root, $"scene-{index}.jpg");
            File.WriteAllBytes(path, [1, 2, (byte)index]);
            images.Add(_database.UpsertImage(new ImageRecord
            {
                SourceId = _image.SourceId, Path = path, FileSize = 3,
                ModifiedUtc = File.GetLastWriteTimeUtc(path), CreatedUtc = start.AddMinutes(index * 20),
                Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow
            }));
        }
        var analyzer = new FakeCloudAnalyzer();
        new CloudVisionRepository(_database).Save(images[1], analyzer, "none", "representative",
            new CloudVisionResult("山景", ["mountain"], [], [], [], null, "none"));

        var clusterService = new SceneTimeClusterService(_database);
        var partial = Assert.Single(clusterService.Build(images));
        Assert.Equal("未分类场景", partial.SceneName);
        Assert.Equal(3, partial.ImageIds.Count);

        var descriptions = new SceneGroupDescriptionService(_database, new CloudVisionRepository(_database));
        var plan = descriptions.BuildPlan(_root, images);
        descriptions.SaveAccepted(plan, new SceneGroupDescription(plan.Fingerprint, "秦岭旅行", "旅行场景",
            ["秦岭"], "fake", "test", "photo-v1", "accepted"));

        var accepted = Assert.Single(clusterService.Build(images));
        Assert.Equal("秦岭旅行", accepted.SceneName);
        Assert.Equal(3, accepted.ImageIds.Count);
    }

    [Fact]
    public void SceneTimeClusters_TolerateLegacyResultsWithNullSceneArrays()
    {
        _image.CreatedUtc = DateTime.UtcNow;
        _database.UpdateImage(_image);
        new CloudVisionRepository(_database).Save(_image, new FakeCloudAnalyzer(), "none", "legacy",
            new CloudVisionResult("旧记录", null!, null!, [], [], null, "none"));

        var cluster = Assert.Single(new SceneTimeClusterService(_database).Build([_image]));

        Assert.Equal("未分类场景", cluster.SceneName);
        Assert.Equal(_image.Id, Assert.Single(cluster.ImageIds));
    }

    [Fact]
    public async Task SceneDescription_SendsRepresentativeDescriptionsToModelInTimeOrder()
    {
        var start = DateTime.UtcNow.AddHours(-3);
        var images = new List<ImageRecord>();
        for (var index = 0; index < 3; index++)
        {
            var path = Path.Combine(_root, $"ordered-{index}.jpg");
            File.WriteAllBytes(path, [7, 8, (byte)index]);
            images.Add(_database.UpsertImage(new ImageRecord
            {
                SourceId = _image.SourceId, Path = path, FileSize = 3,
                ModifiedUtc = File.GetLastWriteTimeUtc(path), CreatedUtc = start.AddHours(2 - index),
                Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow
            }));
        }
        var repository = new CloudVisionRepository(_database);
        var analyzer = new FakeCloudAnalyzer();
        foreach (var image in images)
            repository.Save(image, analyzer, "none", "representative",
                new CloudVisionResult(Path.GetFileNameWithoutExtension(image.Path), ["travel"], [], [], [], null, "none"));
        var service = new SceneGroupDescriptionService(_database, repository);
        var plan = service.BuildPlan(_root, images, maximumSubgroups: 3, maximumRepresentatives: 3);
        var summarizer = new FakeSceneSummarizer();

        var summary = await service.SummarizeWithAiAsync(plan, summarizer);

        Assert.Equal("旅行场景", summary!.SceneName);
        Assert.Equal(["ordered-2", "ordered-1", "ordered-0"], summarizer.Descriptions.Select(item => item.Summary));
        Assert.True(summarizer.Descriptions.Select(item => item.TakenUtc).SequenceEqual(
            summarizer.Descriptions.Select(item => item.TakenUtc).Order()));
    }

    [Fact]
    public async Task CompatibleAnalyzer_SanitizesImageAndSendsStructuredGpsContext()
    {
        var path = Path.Combine(_root, "valid.png");
        using (var image = new Image<Rgb24>(1600, 900, Color.CornflowerBlue)) image.SaveAsPng(path);
        _image.Path = path;
        var handler = new ApiHandler();
        using var client = new HttpClient(handler);
        using var analyzer = new OpenAiCompatibleCloudVisionAnalyzer(new CloudVisionApiOptions(
            new Uri("https://example.test/v1/chat/completions"), "secret-key", "qwen-vl-test", "bailian"), client);
        var location = new PhotoLocationContext(33.159, 103.881, "九寨沟景区", Poi: "五花海",
            NearbyLandmarks: [new("五花海", 420)]);

        var result = await analyzer.AnalyzeAsync(new CloudVisionRequest(_image, location,
            [new("scene.mountain", .8f)]), CancellationToken.None);

        Assert.Contains("九寨沟", result.Summary);
        Assert.Equal("Bearer secret-key", handler.Authorization);
        Assert.Contains("gps_reverse_geocoding", handler.ResponseBody);
        Assert.Contains("五花海", handler.RequestedText);
        Assert.Contains("data:image/jpeg;base64,", handler.RequestBody);
        Assert.DoesNotContain(_image.Path, handler.RequestBody);
    }

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class FakeCloudAnalyzer : ICloudVisionAnalyzer
    {
        public string Provider => "fake";
        public string ModelVersion => "vl-8b-test";
        public string PromptVersion => "photo-v1";
        public int Calls { get; private set; }
        public CloudVisionRequest? LastRequest { get; private set; }

        public Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(new CloudVisionResult("在九寨沟景区附近拍摄的山地照片",
                ["mountain"], [], ["trees"], ["九寨沟", "山景"],
                request.Location?.DisplayName, "gps_reverse_geocoding"));
        }
    }

    private sealed class FakeSceneSummarizer : ISceneGroupSummarizer
    {
        public string Provider => "fake";
        public string ModelVersion => "text-test";
        public string PromptVersion => "scene-test";
        public IReadOnlyList<SceneRepresentativeDescription> Descriptions { get; private set; } = [];
        public Task<SceneSummaryResult> SummarizeAsync(IReadOnlyList<SceneRepresentativeDescription> descriptions,
            CancellationToken cancellationToken = default)
        {
            Descriptions = descriptions;
            return Task.FromResult(new SceneSummaryResult("旅行场景", "按顺序生成的整体总结", ["旅行"]));
        }
    }

    private sealed class FakeGeocoder : IReverseGeocoder
    {
        public Task<ReverseGeocodingResult?> ReverseAsync(double latitude, double longitude, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReverseGeocodingResult?>(new("四川省阿坝州九寨沟景区", "test",
                "中国", "四川省", "阿坝州", "九寨沟县", "九寨沟风景名胜区",
                [new LocationLandmark("五花海", 420)]));
        public Task<GeoBounds?> ResolvePlaceAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<GeoBounds?>(null);
    }

    private sealed class ApiHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = "";
        public string RequestedText { get; private set; } = "";
        public string ResponseBody { get; private set; } = "";
        public string Authorization { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString() ?? "";
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            using (var payload = JsonDocument.Parse(RequestBody))
                RequestedText = payload.RootElement.GetProperty("messages")[1].GetProperty("content")[0]
                    .GetProperty("text").GetString() ?? "";
            var content = JsonSerializer.Serialize(new CloudVisionResult("九寨沟五花海附近的山景照片",
                ["mountain"], [], ["trees"], ["九寨沟"], "九寨沟景区", "gps_reverse_geocoding"));
            ResponseBody = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
