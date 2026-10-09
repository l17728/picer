using Picer.Core.Imaging;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Text;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Geocoding;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Search;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class CloudVisionTests : IDisposable
{
    [Fact]
    public async Task UserCancellationPersistsCompatibleQueuedStateAndReleasesBudgetWithoutCompleting()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new CloudVisionRepository(_database);
        var analyzer = new CancelingAnalyzer(cancellation);
        var service = new CloudVisionAnalysisService(repository, analyzer, new FakeGeocoder());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AnalyzeAsync(
            [(_image, (IReadOnlyList<VisualConceptScore>)[], "test-cancel")], cancellationToken: cancellation.Token));

        Assert.Equal("canceled", repository.Get(_image.Id, analyzer.Provider)!.Value.Status);
        var task = Assert.Single(repository.GetRecentTasks());
        Assert.Equal("canceled", task.Status); Assert.Equal("user_canceled", task.ErrorCode);
        using (var stored = _database.Command("SELECT status FROM cloud_vision_analyses WHERE image_id=$id"))
        { stored.Parameters.AddWithValue("$id", _image.Id); Assert.Equal("queued", stored.ExecuteScalar()); }
        using (var reserved = _database.Command("SELECT COUNT(*) FROM analysis_budget_reservations")) Assert.Equal(0L, reserved.ExecuteScalar());
        using (var charged = _database.Command("SELECT COUNT(*) FROM analysis_billing_events")) Assert.Equal(0L, charged.ExecuteScalar());
        Assert.Null(repository.GetLatestCompleted(_image.Id));

        repository.Queue(_image, analyzer, "", "retry");
        Assert.Equal("queued", repository.Get(_image.Id, analyzer.Provider)!.Value.Status);
        Assert.Null(Assert.Single(repository.GetRecentTasks()).ErrorCode);
        repository.SaveFailure(_image, analyzer, "", "failed", "ordinary_failure");
        Assert.Equal("failed", repository.Get(_image.Id, analyzer.Provider)!.Value.Status);
        Assert.Equal("failed", Assert.Single(repository.GetRecentTasks()).Status);
    }

    private sealed class CancelingAnalyzer(CancellationTokenSource cancellation) : ICloudVisionAnalyzer
    {
        public string Provider => "test-cancel";
        public string ModelVersion => "test";
        public string PromptVersion => "test";
        public Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<CloudVisionResult>(cancellationToken);
        }
    }

    [Fact]
    public async Task SharedDatabaseExecutorWaitsForMirrorTransactionBeforeSavingDescriptions()
    {
        var other = _database.UpsertImage(new ImageRecord { SourceId = _image.SourceId,
            Path = Path.Combine(_root, "mirror-concurrency.jpg"), FileSize = 3,
            ModifiedUtc = _image.ModifiedUtc, Status = ImageStatus.Active });
        using var gate = new SemaphoreSlim(1, 1);
        var queuedSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mirrorActive = false;
        async Task DatabaseWork(Action work, CancellationToken token)
        {
            if (mirrorActive) queuedSave.TrySetResult();
            await gate.WaitAsync(token);
            try { work(); } finally { gate.Release(); }
        }
        var analyzer = new ConcurrentAnalyzer();
        var first = new CloudVisionAnalysisService(new CloudVisionRepository(_database), analyzer,
            new FakeGeocoder(), databaseWork: DatabaseWork);
        var second = new CloudVisionAnalysisService(new CloudVisionRepository(_database), analyzer,
            new FakeGeocoder(), databaseWork: DatabaseWork);
        var firstRun = first.AnalyzeAsync([(_image, (IReadOnlyList<VisualConceptScore>)[], "test")]);
        var secondRun = second.AnalyzeAsync([(other, (IReadOnlyList<VisualConceptScore>)[], "test")]);
        await analyzer.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await gate.WaitAsync();
        try
        {
            using var transaction = _database.BeginTransaction();
            mirrorActive = true;
            analyzer.Release.TrySetResult();
            await queuedSave.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(firstRun.IsCompleted);
            Assert.False(secondRun.IsCompleted);
            transaction.Commit();
        }
        finally { mirrorActive = false; gate.Release(); }
        var results = await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, results.Sum(result => result.Completed));
        Assert.Equal(0, results.Sum(result => result.Failed));
    }

    [Fact]
    public async Task ConcurrentAnalysisOverlapsDistinctContentButReusesSameContent()
    {
        var first = _image;
        first.Sha256 = new string('a', 64);
        _database.UpsertImage(first);
        var other = _database.UpsertImage(new ImageRecord { SourceId = first.SourceId, Path = Path.Combine(_root, "other.jpg"),
            FileSize = 3, ModifiedUtc = first.ModifiedUtc, Sha256 = new string('b', 64), Status = ImageStatus.Active });
        var copy = _database.UpsertImage(new ImageRecord { SourceId = first.SourceId, Path = Path.Combine(_root, "copy.jpg"),
            FileSize = 3, ModifiedUtc = first.ModifiedUtc, Sha256 = first.Sha256, Gps = first.Gps, Status = ImageStatus.Active });
        var analyzer = new ConcurrentAnalyzer();
        var service = new CloudVisionAnalysisService(new CloudVisionRepository(_database), analyzer, new FakeGeocoder(), allowConcurrent: true);
        Task<CloudVisionBatchResult> Run(ImageRecord image) => service.AnalyzeAsync([(image, (IReadOnlyList<VisualConceptScore>)[], "test")]);
        var tasks = new[] { Run(first), Run(other), Run(copy) };
        await analyzer.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, analyzer.Calls);
        analyzer.Release.TrySetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, results.Sum(result => result.Completed));
        Assert.Equal(1, results.Sum(result => result.Skipped));
        Assert.Equal(2, analyzer.Calls);
    }

    private sealed class ConcurrentAnalyzer : ICloudVisionAnalyzer
    {
        public string Provider => "test-concurrent";
        public string ModelVersion => "test";
        public string PromptVersion => "test";
        public int Calls;
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Calls) == 2) BothStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new CloudVisionResult("并发测试照片", [], [], [], []);
        }
    }

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
        Assert.Equal((long)PicerDatabase.CurrentSchemaVersion, (long)version.ExecuteScalar()!);
        using var table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cloud_vision_analyses'";
        Assert.Equal(1L, (long)table.ExecuteScalar()!);
    }

    [Fact]
    public async Task CloudAnalysis_UsesGpsContextAndCachesCompletedResult()
    {
        var analyzer = new FakeCloudAnalyzer();
        var repository = new CloudVisionRepository(_database);
        var people = new PeopleService(_database); var person = people.Create("牟");
        using (var insert = _database.Command("""
            INSERT INTO detected_faces(image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc)
            VALUES($image,'test-face',0,0.1,0.1,0.3,0.3,0.99,$now); SELECT last_insert_rowid();
            """))
        {
            insert.Parameters.AddWithValue("$image", _image.Id); insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            people.ConfirmFace((long)insert.ExecuteScalar()!, person.Id);
        }
        var service = new CloudVisionAnalysisService(repository, analyzer, new FakeGeocoder(), people);
        var candidates = new[] { (_image, (IReadOnlyList<VisualConceptScore>)[new("scene.mountain", .8f)], "complex-scene") };

        var first = await service.AnalyzeAsync(candidates);
        var second = await service.AnalyzeAsync(candidates);

        Assert.Equal(1, first.Completed);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal("九寨沟风景名胜区", analyzer.LastRequest!.Location!.Poi);
        Assert.Null(analyzer.LastRequest.Location.Latitude);
        Assert.Equal("scene.mountain", Assert.Single(analyzer.LastRequest.LocalConcepts).ConceptId);
        Assert.Contains("牟", analyzer.LastRequest.AdditionalContext);
        Assert.Contains("timeSource", analyzer.LastRequest.AdditionalContext);
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
    public void SceneTimeClusters_UseStableAcceptedTitleBeforeRemotePhotoAnalysesArrive()
    {
        var start = DateTime.UtcNow.AddHours(-2);
        _image.CreatedUtc = start;
        _database.UpdateImage(_image);
        var secondPath = Path.Combine(_root, "mirror-second.jpg");
        File.WriteAllBytes(secondPath, [8, 9, 10]);
        var second = _database.UpsertImage(new ImageRecord
        {
            SourceId = _image.SourceId, Path = secondPath, FileSize = 3,
            ModifiedUtc = start.AddMinutes(20), CreatedUtc = start.AddMinutes(20),
            Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow
        });
        var images = new[] { _image, second };
        var descriptions = new SceneGroupDescriptionService(_database, new CloudVisionRepository(_database));
        var plan = descriptions.BuildPlan(_root, images);
        descriptions.SaveAccepted(plan, new SceneGroupDescription(plan.Fingerprint,
            "远程聚餐", "远程镜像先到的场景摘要", ["聚餐"], "test", "e2e", "scene-v1", "accepted"));
        // Simulate a legacy/mirror fingerprint whose SQLite ids differ while
        // the stable directory and time range remain the same.
        using (var command = _database.Command("UPDATE scene_group_descriptions SET fingerprint='legacy-mirror-fingerprint' WHERE fingerprint=$fingerprint"))
        {
            command.Parameters.AddWithValue("$fingerprint", plan.Fingerprint);
            command.ExecuteNonQuery();
        }

        var cluster = Assert.Single(new SceneTimeClusterService(_database).Build(images));
        Assert.Equal("远程聚餐", cluster.SceneName);
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
    public void SceneTimeClusters_PreserveAcceptedTitleForModelSplitSubgroup()
    {
        var start = DateTime.UtcNow.AddHours(-1);
        var images = new List<ImageRecord>();
        for (var index = 0; index < 3; index++)
        {
            var path = Path.Combine(_root, $"split-{index}.jpg"); File.WriteAllBytes(path, [4, 5, (byte)index]);
            images.Add(_database.UpsertImage(new ImageRecord
            {
                SourceId = _image.SourceId, Path = path, FileSize = 3, ModifiedUtc = File.GetLastWriteTimeUtc(path),
                CreatedUtc = start.AddMinutes(index * 10), Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow
            }));
        }
        var analyzer = new FakeCloudAnalyzer(); var cloud = new CloudVisionRepository(_database);
        cloud.Save(images[0], analyzer, "none", "test", new CloudVisionResult("室内", ["indoor"], [], [], []));
        foreach (var image in images.Skip(1))
            cloud.Save(image, analyzer, "none", "test", new CloudVisionResult("爬山", ["mountain"], ["hiking"], [], []));
        var descriptions = new SceneGroupDescriptionService(_database, cloud);
        var subgroup = images.Skip(1).ToArray(); var plan = descriptions.BuildPlan(_root, subgroup);
        descriptions.SaveAccepted(plan, new SceneGroupDescription(plan.Fingerprint, "商洛登山", "两人在商洛登山",
            ["商洛", "登山"], "fake", "test", "scene-v1", "accepted"));

        var clusters = new SceneTimeClusterService(_database).Build(images);

        Assert.Equal(2, clusters.Count);
        Assert.Equal("商洛登山", clusters.Single(cluster => cluster.ImageIds.Count == 2).SceneName);
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
        using (var image = new RasterImage(1600, 900, new RgbPixel(100, 149, 237))) image.SaveAsPng(path);
        _image.Path = path;
        var handler = new ApiHandler();
        using var client = new HttpClient(handler);
        using var analyzer = new OpenAiCompatibleCloudVisionAnalyzer(new CloudVisionApiOptions(
            new Uri("https://example.test/v1/chat/completions"), "secret-key", "qwen-vl-test", "bailian"), client);
        var location = new PhotoLocationContext(33.159, 103.881, "九寨沟景区", Poi: "五花海",
            NearbyLandmarks: [new("五花海", 420)]);

        var result = await analyzer.AnalyzeAsync(new CloudVisionRequest(_image, location,
            [new("scene.mountain", .8f)], "{\"confirmedPeople\":[\"牟\"]}"), CancellationToken.None);

        Assert.Contains("九寨沟", result.Summary);
        Assert.Equal("Bearer secret-key", handler.Authorization);
        Assert.Contains("gps_reverse_geocoding", handler.ResponseBody);
        Assert.Contains("五花海", handler.RequestedText);
        Assert.Contains("confirmedPeople", handler.RequestedText);
        Assert.Contains("牟", handler.RequestedText);
        Assert.Contains("summary必须是言简意赅", handler.SystemText);
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
        public void Dispose() { }
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
        public string SystemText { get; private set; } = "";
        public string ResponseBody { get; private set; } = "";
        public string Authorization { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString() ?? "";
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            using (var payload = JsonDocument.Parse(RequestBody))
            {
                SystemText = payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() ?? "";
                RequestedText = payload.RootElement.GetProperty("messages")[1].GetProperty("content")[0]
                    .GetProperty("text").GetString() ?? "";
            }
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
