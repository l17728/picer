using Picer.Core.Imaging;
using System.Net;
using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Indexing;
using Picer.Core.Logging;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Search;
using Picer.Core.Thumbnails;
using Picer.Core.Video;
using Picer.IndexService;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class GatewayServiceFixture : IDisposable
{
    private const string BearerToken = "picer-test-token";
    public string Root { get; }
    public PicerDatabase Db { get; }
    public ThumbnailService Thumbnails { get; }
    public long SourceId { get; }
    public string BaseUrl { get; }
    public string PipeName { get; }
    public byte[] SpkiPin { get; }
    private readonly WebApplication _app;
    private readonly NamedPipeIndexServer _pipeServer;
    private readonly Task _pipeTask;
    private readonly CancellationTokenSource _pipeCancellation = new();

    public GatewayServiceFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "picer-gateway-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        AppLogger.Initialize(Path.Combine(Root, "logs"));
        Db = new PicerDatabase(Path.Combine(Root, "picer.db"));
        var source = Db.AddSource(Root);
        SourceId = source.Id;

        byte[] pngBytes;
        using (var image = new RasterImage(2, 1, new RgbPixel(255, 255, 255)))
        using (var encoded = new MemoryStream())
        {
            image.SaveAsPng(encoded);
            pngBytes = encoded.ToArray();
        }
        var takenBase = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index < 5; index++)
        {
            var path = Path.Combine(Root, $"img{index}.png");
            File.WriteAllBytes(path, pngBytes);
            Db.UpsertImage(new ImageRecord
            {
                SourceId = SourceId,
                Path = path,
                FileSize = pngBytes.Length + index,
                ModifiedUtc = takenBase.AddMinutes(index),
                TakenUtc = takenBase.AddMinutes(index),
                IndexedUtc = takenBase,
                Width = 2,
                Height = 1,
                Status = ImageStatus.Active
            });
        }
        var person = new PeopleService(Db).Create("测试人物");
        var firstImage = Db.GetImages(SourceId).First();
        using (var face = Db.Command("INSERT INTO detected_faces(image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,embedding,created_utc) VALUES($image,'test-model',0,.1,.1,.5,.5,.99,$embedding,$created)"))
        {
            face.Parameters.AddWithValue("$image", firstImage.Id);
            face.Parameters.AddWithValue("$embedding", new byte[] { 1, 2, 3, 4 });
            face.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("o"));
            face.ExecuteNonQuery();
        }
        var faceId = Convert.ToInt64(Db.Command("SELECT last_insert_rowid()").ExecuteScalar());
        using (var assignment = Db.Command("INSERT INTO face_person_assignments(face_id,person_id,state,confidence,assigned_by,updated_utc) VALUES($face,$person,'confirmed',.99,'user',$updated)"))
        {
            assignment.Parameters.AddWithValue("$face", faceId);
            assignment.Parameters.AddWithValue("$person", person.Id);
            assignment.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("o"));
            assignment.ExecuteNonQuery();
        }

        Thumbnails = new ThumbnailService(Path.Combine(Root, "thumbs"));
        var instance = IndexServiceHost.Build(Root, port: 0, bearerToken: BearerToken);
        _app = instance.App;
        BaseUrl = IndexServiceHost.StartAsync(_app).GetAwaiter().GetResult();
        SpkiPin = ServiceSecurity.ComputeSpkiPin(instance.Certificate);
        PipeName = $"picer-test-{Guid.NewGuid():N}";
        _pipeServer = new NamedPipeIndexServer(PipeName, BearerToken, instance.Handlers, instance.Engine, instance.Jobs);
        _pipeTask = _pipeServer.RunAsync();
        Engine = instance.Engine;
    }

    public IndexEngine Engine { get; }

    public IIndexGateway CreateGateway(string transport) => transport switch
    {
        "http" => CreateHttpGateway(),
        "pipe" => CreatePipeGateway(),
        _ => CreateLocalGateway()
    };

    public IIndexGateway CreateLocalGateway() => new LocalGateway(Db, Thumbnails, Engine);

    public IIndexGateway CreateHttpGateway() => new HttpIndexGateway(BaseUrl, BearerToken, SpkiPin);

    public IIndexGateway CreatePipeGateway() => new PipeIndexGateway(PipeName, BearerToken);

    public void MarkImageMissing(long id)
    {
        var record = Db.GetImage(id);
        Assert.NotNull(record);
        record.Status = ImageStatus.Missing;
        Db.UpsertImage(record);
    }

    public void MarkImageActive(long id)
    {
        var record = Db.GetImage(id);
        Assert.NotNull(record);
        record.Status = ImageStatus.Active;
        Db.UpsertImage(record);
    }

    public void Dispose()
    {
        _pipeServer.Dispose();
        try { _pipeTask.Wait(2000); } catch { }
        _pipeCancellation.Dispose();
        _app.StopAsync().GetAwaiter().GetResult();
        _app.DisposeAsync().GetAwaiter().GetResult();
        Db.Dispose();
        try { Directory.Delete(Root, true); } catch { }
    }
}

[CollectionDefinition(nameof(GatewayContractCollection), DisableParallelization = true)]
public sealed class GatewayContractCollection { }

[Collection(nameof(GatewayContractCollection))]
public sealed class GatewayContractTests(GatewayServiceFixture fixture) : IClassFixture<GatewayServiceFixture>
{
    [Fact]
    public async Task FaceReviewDecisionEndpoint_AcknowledgesAndProtectsAgainstOldSnapshots()
    {
        using var isolated = new GatewayServiceFixture();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(isolated.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "picer-test-token");
        var face = isolated.Db.GetAllMirroredFaces().First();
        var decision = new GatewayFaceReviewDecision(face.Id, "ignored", DateTime.UtcNow.ToString("O"));
        using var response = await client.PostAsJsonAsync("api/sync/v1/face-review-decisions", new[] { decision });
        response.EnsureSuccessStatusCode();
        Assert.Contains(decision, (await response.Content.ReadFromJsonAsync<GatewayFaceReviewDecision[]>())!);
        isolated.Db.ReplaceMirroredFaceWorkflow(new GatewayMetadata([], [], [], [], [], [], [], []));
        Assert.Contains(isolated.Db.GetAllMirroredFaceReviewStates(), item => item.FaceId == face.Id);
        using var invalid = await client.PostAsJsonAsync("api/sync/v1/face-review-decisions", new[] { decision with { State = "invalid" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    public static IEnumerable<object[]> Transports()
    {
        yield return ["local"];
        yield return ["http"];
        yield return ["pipe"];
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Health_ReportsCurrentSchemaVersion(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        var health = await gateway.GetHealthAsync();
        Assert.Equal("ok", health.Status);
        Assert.Equal(PicerDatabase.CurrentSchemaVersion, health.SchemaVersion);
    }

    [Fact]
    public async Task Version_ReportsAssemblyAndOptionalDeploymentIdentityWithoutAuthentication()
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl) };

        using var response = await client.GetAsync("/version");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal("Picer.IndexService", root.GetProperty("service").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("assemblyVersion").GetString()));
        Assert.True(root.GetProperty("startedUtc").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("binarySha256").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("deploymentId").ValueKind);
    }

    [Fact]
    public async Task Metadata_MirrorsTheAppliedFaceArtifactAndItsModelScopedPrototype()
    {
        var personId = new PeopleService(fixture.Db).GetAll().Single().Id;
        const string modelVersion = "insightface-arcface";
        var created = DateTime.UtcNow.ToString("O");
        using (var artifact = fixture.Db.Command("""
            INSERT INTO face_training_artifacts
                (artifact_id,dataset_id,endpoint,model_version,artifact_json,false_accept_rate,false_reject_rate,state,created_utc,applied_utc)
            VALUES('insight-active-fixture','fixture','modelserver',$model,'{}',.01,.02,'active',$created,$created)
            """))
        {
            artifact.Parameters.AddWithValue("$model", modelVersion);
            artifact.Parameters.AddWithValue("$created", created);
            artifact.ExecuteNonQuery();
        }
        using (var prototype = fixture.Db.Command("""
            INSERT INTO face_person_prototypes(person_id,centroid,threshold,sample_count,model_version,updated_utc)
            VALUES($person,$centroid,.71,4,$model,$created)
            """))
        {
            prototype.Parameters.AddWithValue("$person", personId);
            prototype.Parameters.AddWithValue("$centroid", new byte[] { 1, 2, 3, 4 });
            prototype.Parameters.AddWithValue("$model", modelVersion);
            prototype.Parameters.AddWithValue("$created", created);
            prototype.ExecuteNonQuery();
        }

        using var gateway = (HttpIndexGateway)fixture.CreateHttpGateway();
        var defaultMetadata = await gateway.GetMetadataAsync();
        Assert.Null(defaultMetadata.FaceTrainingArtifacts);
        var metadata = await gateway.GetMetadataAsync(CancellationToken.None, false, true);

        Assert.Contains(metadata.FaceTrainingArtifactsOrEmpty,
            item => item.ArtifactId == "insight-active-fixture" && item.State == "active");
        var mirroredPrototype = Assert.Single(metadata.FacePersonPrototypesOrEmpty);
        Assert.Equal(personId, mirroredPrototype.PersonId);
        Assert.Equal(modelVersion, mirroredPrototype.ModelVersion);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, mirroredPrototype.Centroid);

        using (var setting = fixture.Db.Command("""
            INSERT INTO face_model_settings(model_version,match_threshold,calibrated_same_pairs,calibrated_different_pairs,updated_utc)
            VALUES($model,.73,12,18,$created)
            """))
        {
            setting.Parameters.AddWithValue("$model", modelVersion);
            setting.Parameters.AddWithValue("$created", created);
            setting.ExecuteNonQuery();
        }
        fixture.Db.UpsertMirroredFaceTrainingMetadata(metadata with
        {
            FaceModelSettings = [new GatewayFaceModelSetting(modelVersion, .73, 12, 18, created)]
        });
        var repository = new VisionAnalysisRepository(fixture.Db);
        Assert.Equal("insight-active-fixture", repository.GetActiveFaceAdapterVersion(modelVersion));
        Assert.Equal(.73, repository.GetFaceMatchThreshold(modelVersion));
        Assert.Single(repository.GetTrainedFacePrototypes(modelVersion));
    }

    [Fact]
    public async Task ServiceCapabilities_ReportAgentBackedStates()
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "picer-test-token");
        using var response = await client.GetAsync("api/service/v1/capabilities");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("service", document.RootElement.GetProperty("mode").GetString());
        var capabilities = document.RootElement.GetProperty("capabilities");
        Assert.Contains(capabilities.EnumerateArray(), item => item.GetProperty("id").GetString() == "ffmpeg.keyframes");
        Assert.Contains(capabilities.EnumerateArray(), item => item.GetProperty("id").GetString() == "ffmpeg.audio_extract");
        Assert.Contains(capabilities.EnumerateArray(), item => item.GetProperty("id").GetString() == "asr.transcribe");
    }

    [Fact]
    public async Task ServiceStatusPage_AndConfig_DoNotExposeCredentials()
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "picer-test-token");
        using var page = await client.GetAsync("service");
        page.EnsureSuccessStatusCode();
        Assert.Contains("Picer", await page.Content.ReadAsStringAsync());
        using var config = await client.GetAsync("api/service/v1/config");
        config.EnsureSuccessStatusCode();
        var text = await config.Content.ReadAsStringAsync();
        Assert.Contains("exposedToClient", text);
        Assert.DoesNotContain("apiKey", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServiceConfig_RejectsSecrets_AndPersistsVersionHistory()
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "picer-test-token");
        using var rejected = await client.PutAsJsonAsync("api/service/v1/config", new { apiKey = "must-not-store" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var accepted = await client.PutAsJsonAsync("api/service/v1/config", new { asrModel = "qwen3-asr-1.7b", revision = "test" });
        accepted.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var version = payload.RootElement.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        using var history = await client.GetAsync("api/service/v1/config/history");
        history.EnsureSuccessStatusCode();
        Assert.Contains(version!, await history.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task ListImages_KeysetPaginationCoversAllItemsExactlyOnce(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        var collected = new List<long>();
        string? cursor = null;
        var totalCount = 0;
        do
        {
            var page = await gateway.ListImagesAsync(sourceId: fixture.SourceId, includeMissing: false, pageSize: 2, cursor: cursor);
            Assert.Equal(5, page.TotalCount);
            totalCount += page.Items.Count;
            collected.AddRange(page.Items.Select(item => item.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(5, totalCount);
        Assert.Equal(5, collected.Count);
        Assert.Equal(collected.Count, collected.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task GetImage_ReturnsRecordOrNotFound(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        var page = await gateway.ListImagesAsync(sourceId: fixture.SourceId, includeMissing: false, pageSize: 1);
        var expected = page.Items.Single();
        var actual = await gateway.GetImageAsync(expected.Id);
        Assert.NotNull(actual);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Path, actual.Path);
        Assert.Null(await gateway.GetImageAsync(99_999_999));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Metadata_LocalAndRemoteTransportsExposeTheSameFaceAndRelationshipSet(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        var metadata = await gateway.GetMetadataAsync();
        Assert.Single(metadata.Faces);
        Assert.Single(metadata.FaceAssignments);
        Assert.Equal(metadata.Faces.Select(face => face.Id), metadata.FaceAssignments.Select(assignment => assignment.FaceId));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Thumbnail_ReturnsDecodableBytes(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        var page = await gateway.ListImagesAsync(sourceId: fixture.SourceId, includeMissing: false, pageSize: 1);
        var id = page.Items.Single().Id;
        using var stream = await gateway.OpenThumbnailAsync(id);
        Assert.NotNull(stream);
        var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        Assert.True(bytes.Length > 0);
        var image = RasterImage.Load(bytes.ToArray());
        Assert.True(image.Width > 0);
    }

    [Fact]
    public async Task PersistedServiceConfig_IsReplayedAfterMediaAgentReconnectAndServiceRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-agent-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string endpoint = "http://127.0.0.1:18655/face";
        try
        {
            var first = IndexServiceHost.Build(root, port: 0, bearerToken: "picer-test-token");
            var firstUrl = await IndexServiceHost.StartAsync(first.App);
            using (var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
            using (var client = new HttpClient(handler) { BaseAddress = new Uri(firstUrl) })
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "picer-test-token");
                using var response = await client.PutAsync("api/service/v1/config", new StringContent(
                    JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["PICER_LOCAL_FACE_ENDPOINT"] = endpoint,
                        ["PICER_LOCAL_ASR_ENDPOINT"] = "http://127.0.0.1:18656/asr"
                    }),
                    Encoding.UTF8, "application/json"));
                response.EnsureSuccessStatusCode();
                using var partialUpdate = await client.PutAsync("api/service/v1/config", new StringContent(
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["PICER_LOCAL_FACE_ENDPOINT"] = "http://127.0.0.1:18657/face" }),
                    Encoding.UTF8, "application/json"));
                partialUpdate.EnsureSuccessStatusCode();
            }
            await first.App.StopAsync();
            await first.App.DisposeAsync();

            var second = IndexServiceHost.Build(root, port: 0, bearerToken: "picer-test-token");
            var secondUrl = await IndexServiceHost.StartAsync(second.App);
            try
            {
                using var agent = new ClientWebSocket();
                agent.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                agent.Options.SetRequestHeader("Authorization", "Bearer picer-test-token");
                var serviceUri = new UriBuilder(new Uri(secondUrl)) { Scheme = "wss" }.Uri;
                await agent.ConnectAsync(new Uri(serviceUri, "api/agent/v1/connect"), CancellationToken.None);
                await SendWsAsync(agent, new { type = "agent.register", agentId = "reconnected-agent" });
                Assert.Contains("agent.registered", await ReceiveWsAsync(agent), StringComparison.Ordinal);
                using var config = JsonDocument.Parse(await ReceiveWsAsync(agent));
                Assert.Equal("agent.config", config.RootElement.GetProperty("type").GetString());
                var appliedConfig = config.RootElement.GetProperty("config");
                Assert.Equal("http://127.0.0.1:18657/face", appliedConfig.GetProperty("PICER_LOCAL_FACE_ENDPOINT").GetString());
                Assert.Equal("http://127.0.0.1:18656/asr", appliedConfig.GetProperty("PICER_LOCAL_ASR_ENDPOINT").GetString());
            }
            finally
            {
                await second.App.StopAsync();
                await second.App.DisposeAsync();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RemoteAgent_ProxiesOriginalImageThroughIndexService()
    {
        var image = fixture.Db.GetImages(fixture.SourceId).First();
        var agentId = "test-agent-" + Guid.NewGuid().ToString("N");
        using var agent = new ClientWebSocket();
        agent.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        agent.Options.SetRequestHeader("Authorization", "Bearer picer-test-token");
        var serviceUri = new UriBuilder(new Uri(fixture.BaseUrl)) { Scheme = "wss" }.Uri;
        await agent.ConnectAsync(new Uri(serviceUri, "api/agent/v1/connect"), CancellationToken.None);
        await SendWsAsync(agent, new { type = "agent.register", agentId });
        var registered = await ReceiveWsAsync(agent);
        Assert.Contains("agent.registered", registered, StringComparison.Ordinal);
        var responseTask = Task.Run(async () =>
        {
            var request = JsonDocument.Parse(await ReceiveAgentRequestAsync(agent)).RootElement;
            var requestId = request.GetProperty("requestId").GetString();
            await SendWsAsync(agent, new
            {
                type = "asset.response",
                requestId,
                contentType = "image/png",
                contentBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(image.Path))
            });
        });

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "picer-test-token");
        using var response = await client.GetAsync($"api/gateway/v1/assets/images/{image.Id}?agentId={Uri.EscapeDataString(agentId)}");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"asset proxy failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var actual = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(await File.ReadAllBytesAsync(image.Path), actual);
        await responseTask;

        // A remote GUI may not know the source host's agent id.  With one
        // online agent the service must safely select that session.
        var fallbackTask = Task.Run(async () =>
        {
            var request = JsonDocument.Parse(await ReceiveAgentRequestAsync(agent)).RootElement;
            var requestId = request.GetProperty("requestId").GetString();
            await SendWsAsync(agent, new
            {
                type = "asset.response",
                requestId,
                contentType = "image/png",
                contentBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(image.Path))
            });
        });
        using var fallbackResponse = await client.GetAsync($"api/gateway/v1/assets/images/{image.Id}");
        fallbackResponse.EnsureSuccessStatusCode();
        Assert.Equal(await File.ReadAllBytesAsync(image.Path), await fallbackResponse.Content.ReadAsByteArrayAsync());
        await fallbackTask;
    }

    [Fact]
    public async Task RemoteAgent_ProxiesOriginalVideoWithoutClientAgentId()
    {
        var videoPath = Path.Combine(fixture.Root, "remote-proxy-test.mp4");
        var expected = Encoding.UTF8.GetBytes("test video payload via source agent");
        await File.WriteAllBytesAsync(videoPath, expected);
        var video = new Picer.Core.Video.VideoRepository(fixture.Db).Upsert(fixture.SourceId, videoPath);
        var agentId = "test-video-agent-" + Guid.NewGuid().ToString("N");
        using var agent = new ClientWebSocket();
        agent.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        agent.Options.SetRequestHeader("Authorization", "Bearer picer-test-token");
        var serviceUri = new UriBuilder(new Uri(fixture.BaseUrl)) { Scheme = "wss" }.Uri;
        await agent.ConnectAsync(new Uri(serviceUri, "api/agent/v1/connect"), CancellationToken.None);
        await SendWsAsync(agent, new { type = "agent.register", agentId });
        Assert.Contains("agent.registered", await ReceiveWsAsync(agent), StringComparison.Ordinal);

        var responseTask = Task.Run(async () =>
        {
            var request = JsonDocument.Parse(await ReceiveAgentRequestAsync(agent)).RootElement;
            Assert.Contains("remote-proxy-test.mp4", request.GetProperty("path").GetString(), StringComparison.OrdinalIgnoreCase);
            await SendWsAsync(agent, new
            {
                type = "asset.response",
                requestId = request.GetProperty("requestId").GetString(),
                contentType = "video/mp4",
                contentBase64 = Convert.ToBase64String(expected)
            });
        });

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "picer-test-token");
        using var response = await client.GetAsync($"api/gateway/v1/assets/videos/{video.Id}");
        response.EnsureSuccessStatusCode();
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
        await responseTask;
    }

    [Fact]
    public async Task RemoteVideoPeople_UsesAgentAndWritesDetectionsToServiceIndex()
    {
        var videoPath = Path.Combine(fixture.Root, "remote-face-analysis.mp4");
        await File.WriteAllBytesAsync(videoPath, [1, 2, 3]);
        var videoRepository = new VideoRepository(fixture.Db);
        var video = videoRepository.Upsert(fixture.SourceId, videoPath);
        videoRepository.SaveKeyframes(video.Id, [new VideoKeyframe(1.25, videoPath, 0)]);
        var keyframe = videoRepository.GetKeyframes(video.Id, existingOnly: false).Single();
        var agentId = "test-video-face-agent-" + Guid.NewGuid().ToString("N");
        using var agent = new ClientWebSocket();
        agent.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        agent.Options.SetRequestHeader("Authorization", "Bearer picer-test-token");
        var serviceUri = new UriBuilder(new Uri(fixture.BaseUrl)) { Scheme = "wss" }.Uri;
        await agent.ConnectAsync(new Uri(serviceUri, "api/agent/v1/connect"), CancellationToken.None);
        await SendWsAsync(agent, new { type = "agent.register", agentId, capabilities = new[] { "face.embed", "video.people" } });
        Assert.Contains("agent.registered", await ReceiveWsAsync(agent), StringComparison.Ordinal);

        var task = Task.Run(async () =>
        {
            using var request = JsonDocument.Parse(await ReceiveAgentRequestAsync(agent));
            Assert.Equal("task.open", request.RootElement.GetProperty("type").GetString());
            Assert.Equal("video.people", request.RootElement.GetProperty("task").GetString());
            using var requestPayload = JsonDocument.Parse(request.RootElement.GetProperty("payload").GetString()!);
            Assert.Equal("insightface-arcface", requestPayload.RootElement.GetProperty("modelVersion").GetString());
            Assert.Equal(["insightface-arcface", "adaface", "arcface-paddle", "facex-zoo", "arcface", "sface"],
                requestPayload.RootElement.GetProperty("modelPriority").EnumerateArray().Select(item => item.GetString()).ToArray());
            var result = new FaceServiceResult(FaceServiceContract.CurrentSchema, "video.people", "insightface-arcface",
            [new FaceServiceFace(0, 1.25, .2f, .2f, .3f, .3f, .99f, [1f, 0f, 0f], keyframe.Id)]);
            await SendWsAsync(agent, new
            {
                type = "task.result",
                requestId = request.RootElement.GetProperty("requestId").GetString(),
                payload = JsonSerializer.Serialize(result)
            });
        });

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "picer-test-token");
        using var response = await client.PostAsync($"api/service/v1/video-people/{video.Id}?agentId={Uri.EscapeDataString(agentId)}", content: null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"remote video people analysis failed: {(int)response.StatusCode} {body}");
        await task;
        using var command = fixture.Db.Command("SELECT COUNT(*) FROM video_detected_faces WHERE video_id=$video AND model_version='insightface-arcface'");
        command.Parameters.AddWithValue("$video", new VideoContentRepository(fixture.Db).GetContentId(video.Id));
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
        using var status = fixture.Db.Command("SELECT person_analysis_status FROM videos WHERE id=$video");
        status.Parameters.AddWithValue("$video", video.Id);
        Assert.Equal("completed", status.ExecuteScalar());

        using var mirrorDb = new PicerDatabase(Path.Combine(fixture.Root, "local-mirror.db"));
        var localSource = mirrorDb.AddSource(fixture.Root);
        new VideoRepository(mirrorDb).UpsertMirroredBatch([videoRepository.GetAll().Single(item => item.Id == video.Id)], localSource.Id);
        using var mirrorGateway = (HttpIndexGateway)fixture.CreateHttpGateway();
        var metadata = await mirrorGateway.GetMetadataAsync(CancellationToken.None, includeEmbeddings: false, includeFaceModels: true);
        Assert.Contains(metadata.VideoFacesOrEmpty, face => face.VideoId == video.Id);
        mirrorDb.UpsertMirroredVideoMetadata(metadata);
        using var mirroredFaces = mirrorDb.Command("SELECT COUNT(*) FROM video_detected_faces WHERE video_id=$video AND model_version='insightface-arcface'");
        mirroredFaces.Parameters.AddWithValue("$video", new VideoContentRepository(mirrorDb).GetContentId(video.Id));
        Assert.Equal(1L, Convert.ToInt64(mirroredFaces.ExecuteScalar()));
    }

    [Fact]
    public async Task LateVideoKeyframeResponseCannotAttachToReplacementContent()
    {
        var path = Path.Combine(fixture.Root, "late-keyframe-" + Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllBytesAsync(path, [8, 9, 10]);
        var repository = new VideoRepository(fixture.Db);
        var video = repository.Upsert(fixture.SourceId, path);
        var agentId = "late-keyframe-agent-" + Guid.NewGuid().ToString("N");
        using var agent = new ClientWebSocket();
        agent.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        agent.Options.SetRequestHeader("Authorization", "Bearer picer-test-token");
        var serviceUri = new UriBuilder(new Uri(fixture.BaseUrl)) { Scheme = "wss" }.Uri;
        await agent.ConnectAsync(new Uri(serviceUri, "api/agent/v1/connect"), CancellationToken.None);
        await SendWsAsync(agent, new { type = "agent.register", agentId, capabilities = new[] { "ffmpeg.keyframes" } });
        Assert.Contains("agent.registered", await ReceiveWsAsync(agent), StringComparison.Ordinal);
        var task = Task.Run(async () =>
        {
            using var request = JsonDocument.Parse(await ReceiveAgentRequestAsync(agent));
            // The source identity changes while the remote worker still holds the old request.
            repository.UpsertMirrored(video with { Sha256 = new string('d', 64) }, fixture.SourceId);
            await SendWsAsync(agent, new
            {
                type = "task.result",
                requestId = request.RootElement.GetProperty("requestId").GetString(),
                payload = JsonSerializer.Serialize(new[] { new VideoKeyframe(1, "old-frame.jpg", 0) })
            });
        });
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "picer-test-token");
        using var response = await client.PostAsync($"api/service/v1/video-keyframes/{video.Id}?agentId={Uri.EscapeDataString(agentId)}", null);
        await task;
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("video_content_changed", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(repository.GetKeyframes(video.Id, false));
    }

    [Fact]
    public async Task RemoteVideoPeople_AcceptsOnePrioritizedFallbackModelWithoutMixingSpaces()
    {
        var videoPath = Path.Combine(fixture.Root, "remote-fallback-face-analysis.mp4");
        await File.WriteAllBytesAsync(videoPath, [1, 2, 3]);
        var videoRepository = new VideoRepository(fixture.Db);
        var video = videoRepository.Upsert(fixture.SourceId, videoPath);
        videoRepository.SaveKeyframes(video.Id, [new VideoKeyframe(2.5, videoPath, 0)]);
        var keyframe = videoRepository.GetKeyframes(video.Id, existingOnly: false).Single();
        var agentId = "test-video-face-fallback-agent-" + Guid.NewGuid().ToString("N");
        using var agent = new ClientWebSocket();
        agent.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        agent.Options.SetRequestHeader("Authorization", "Bearer picer-test-token");
        var serviceUri = new UriBuilder(new Uri(fixture.BaseUrl)) { Scheme = "wss" }.Uri;
        await agent.ConnectAsync(new Uri(serviceUri, "api/agent/v1/connect"), CancellationToken.None);
        await SendWsAsync(agent, new { type = "agent.register", agentId, capabilities = new[] { "face.embed", "video.people" } });
        Assert.Contains("agent.registered", await ReceiveWsAsync(agent), StringComparison.Ordinal);

        var task = Task.Run(async () =>
        {
            using var request = JsonDocument.Parse(await ReceiveAgentRequestAsync(agent));
            using var requestPayload = JsonDocument.Parse(request.RootElement.GetProperty("payload").GetString()!);
            Assert.Equal("insightface-arcface", requestPayload.RootElement.GetProperty("modelVersion").GetString());
            Assert.Equal("sface", requestPayload.RootElement.GetProperty("modelPriority").EnumerateArray().Last().GetString());
            var fallback = new FaceServiceResult(FaceServiceContract.CurrentSchema, "video.people", "sface",
                [new FaceServiceFace(0, 2.5, .1f, .2f, .25f, .3f, .98f, [0f, 1f, 0f], keyframe.Id)]);
            await SendWsAsync(agent, new
            {
                type = "task.result",
                requestId = request.RootElement.GetProperty("requestId").GetString(),
                payload = JsonSerializer.Serialize(fallback)
            });
        });

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "picer-test-token");
        using var response = await client.PostAsync($"api/service/v1/video-people/{video.Id}?agentId={Uri.EscapeDataString(agentId)}", content: null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"prioritized fallback analysis failed: {(int)response.StatusCode} {body}");
        await task;

        using var insightCount = fixture.Db.Command("SELECT COUNT(*) FROM video_detected_faces WHERE video_id=$video AND model_version='insightface-arcface'");
        insightCount.Parameters.AddWithValue("$video", new VideoContentRepository(fixture.Db).GetContentId(video.Id));
        Assert.Equal(0L, Convert.ToInt64(insightCount.ExecuteScalar()));
        using var fallbackCount = fixture.Db.Command("SELECT COUNT(*) FROM video_detected_faces WHERE video_id=$video AND model_version='sface'");
        fallbackCount.Parameters.AddWithValue("$video", new VideoContentRepository(fixture.Db).GetContentId(video.Id));
        Assert.Equal(1L, Convert.ToInt64(fallbackCount.ExecuteScalar()));
        using var status = fixture.Db.Command("SELECT person_analysis_status FROM videos WHERE id=$video");
        status.Parameters.AddWithValue("$video", video.Id);
        Assert.Equal("completed", status.ExecuteScalar());
    }

    [Fact]
    public async Task SyncImages_UpsertsBatchAndReturnsAcceptedCount()
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "picer-test-token");
        var source = fixture.Db.GetSources().Single();
        var image = fixture.Db.GetImages(fixture.SourceId).First();
        using var response = await client.PostAsJsonAsync("api/sync/v1/images", new { sourcePath = source.Path, images = new[] { image } });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, payload.GetProperty("accepted").GetInt32());
        Assert.True(payload.GetProperty("sourceId").GetInt64() > 0);
    }

    [Fact]
    public async Task IncrementalSync_IsIdempotentAndPublishesChangedMetadata()
    {
        var clientRoot = Path.Combine(Path.GetTempPath(), "picer-sync-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(clientRoot);
        var checkpoint = Path.Combine(clientRoot, "sync-checkpoint.json");
        try
        {
            using var clientDb = new PicerDatabase(Path.Combine(clientRoot, "picer.db"));
            var source = clientDb.AddSource(clientRoot);
            var imagePath = Path.Combine(clientRoot, "sync.png");
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
            clientDb.UpsertImage(new ImageRecord
            {
                SourceId = source.Id, Path = imagePath, FileSize = 3,
                ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow,
                Width = 1, Height = 1, Status = ImageStatus.Active
            });
            using var sync = IndexSyncClient.Create(fixture.BaseUrl, "picer-test-token", fixture.SpkiPin);
            var first = await sync.PushIncrementalAsync(clientDb, checkpoint);
            var second = await sync.PushIncrementalAsync(clientDb, checkpoint);
            Assert.True(first.Changed);
            Assert.False(second.Changed);
            Assert.True(first.Images > 0);

            var record = clientDb.GetImages(source.Id).Single();
            record.Camera = "sync-camera";
            clientDb.UpsertImage(record);
            var third = await sync.PushIncrementalAsync(clientDb, checkpoint);
            Assert.True(third.Changed);
            Assert.NotEqual(first.MediaFingerprint, third.MediaFingerprint);
        }
        finally
        {
            try { Directory.Delete(clientRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task DirectoryClassificationPreferences_RoundTripThroughService()
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "picer-test-token");
        var values = new Dictionary<string, bool> { ["C:\\Photos"] = true, ["C:\\Photos\\Trips"] = false };
        using var post = await client.PostAsJsonAsync("api/sync/v1/directory-classification", new { overrides = values });
        post.EnsureSuccessStatusCode();
        var roundTrip = await client.GetFromJsonAsync<Dictionary<string, bool>>("api/gateway/v1/directory-classification");
        Assert.NotNull(roundTrip);
        Assert.Equal(values, roundTrip);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task DirectoryBrowse_LocalAndRemoteMirrorHaveIdenticalSceneSequence()
    {
        // This is the regression contract for the real directory-browser path:
        // local clustering is the reference answer, while the remote side first
        // receives the same image and scene metadata and then rebuilds its tree.
        var mirrorRoot = Path.Combine(Path.GetTempPath(), "picer-scene-mirror-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mirrorRoot);
        using var parityFixture = new GatewayServiceFixture();
        try
        {
            var source = parityFixture.Db.GetSources().Single();
            var browsePath = Path.Combine(source.Path, "scene-parity");
            Directory.CreateDirectory(browsePath);
            var start = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
            var images = new List<ImageRecord>();
            for (var index = 0; index < 4; index++)
            {
                var path = Path.Combine(browsePath, $"scene-parity-{index}.png");
                await File.WriteAllBytesAsync(path, [1, 2, 3, (byte)index]);
                images.Add(parityFixture.Db.UpsertImage(new ImageRecord
                {
                    SourceId = source.Id, Path = path, FileSize = 4,
                    ModifiedUtc = start.AddHours(index < 2 ? index * .25 : 5 + (index - 2) * .25),
                    TakenUtc = start.AddHours(index < 2 ? index * .25 : 5 + (index - 2) * .25),
                    IndexedUtc = start, Width = 2, Height = 2, Status = ImageStatus.Active
                }));
            }

            var descriptions = new SceneGroupDescriptionService(parityFixture.Db, new CloudVisionRepository(parityFixture.Db));
            var firstGroup = images.Take(2).ToArray();
            var secondGroup = images.Skip(2).ToArray();
            var firstPlan = descriptions.BuildPlan(browsePath, firstGroup);
            descriptions.SaveAccepted(firstPlan, new SceneGroupDescription(firstPlan.Fingerprint,
                "本地聚餐", "本地聚餐摘要", ["聚餐"], "test", "e2e", "scene-v1", "accepted"));
            var secondPlan = descriptions.BuildPlan(browsePath, secondGroup);
            descriptions.SaveAccepted(secondPlan, new SceneGroupDescription(secondPlan.Fingerprint,
                "本地登山", "本地登山摘要", ["登山"], "test", "e2e", "scene-v1", "accepted"));

            using var http = parityFixture.CreateHttpGateway();
            var remotePage = await http.ListImagesAsync(source.Id, includeMissing: false, pageSize: 500);
            Assert.Contains(remotePage.Items, item => item.Path.EndsWith("scene-parity-0.png", StringComparison.OrdinalIgnoreCase));

            using var mirror = new PicerDatabase(Path.Combine(mirrorRoot, "remote-mirror.db"));
            mirror.AddSource(source.Path);
            foreach (var image in parityFixture.Db.GetImages(source.Id, includeMissing: false))
                mirror.UpsertMirroredImage(image);
            mirror.UpsertMirroredVideoMetadata(new GatewayMetadata(
                [], [], [], [], [], [], [], [], [], [], parityFixture.Db.GetAllMirroredSceneGroupDescriptions()));

            var localClusters = new SceneTimeClusterService(parityFixture.Db).Build(browsePath, includeDescendants: false);
            var remoteImages = mirror.GetActiveImagesInDirectory(browsePath, includeDescendants: false);
            var remoteClusters = new SceneTimeClusterService(mirror).Build(remoteImages);
            var localSnapshot = localClusters.Select(cluster => (cluster.SceneName, cluster.ImageIds.Count,
                cluster.ImageIds.ToArray())).ToArray();
            var remoteSnapshot = remoteClusters.Select(cluster => (cluster.SceneName, cluster.ImageIds.Count,
                cluster.ImageIds.ToArray())).ToArray();
            Assert.Equal(localSnapshot.Length, remoteSnapshot.Length);
            for (var index = 0; index < localSnapshot.Length; index++)
            {
                Assert.Equal(localSnapshot[index].SceneName, remoteSnapshot[index].SceneName);
                Assert.Equal(localSnapshot[index].Item2, remoteSnapshot[index].Item2);
                Assert.Equal(localSnapshot[index].Item3, remoteSnapshot[index].Item3);
            }
            Assert.Equal(["本地聚餐", "本地登山"], remoteClusters.Select(cluster => cluster.SceneName));
        }
        finally
        {
            try { Directory.Delete(mirrorRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task SyncVideos_UpsertsMetadataAndListsThroughGateway()
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "picer-test-token");
        var source = fixture.Db.GetSources().Single();
        var path = Path.Combine(source.Path, "remote-video.mp4");
        var video = new Picer.Core.Video.VideoRecord(0, fixture.SourceId, path, 1234, DateTime.UtcNow, 4.5, 1920, 1080, DateTime.UtcNow, "31,121", "active");
        using var response = await client.PostAsJsonAsync("api/sync/v1/videos", new { sourcePath = source.Path, videos = new[] { video } });
        response.EnsureSuccessStatusCode();
        using var gateway = fixture.CreateHttpGateway();
        var page = await gateway.ListVideosAsync(fixture.SourceId, 20);
        var actual = Assert.Single(page.Items, item => item.Path == path);
        Assert.Equal(4.5, actual.DurationSeconds);
        Assert.Equal(1920, actual.Width);
    }

    [Fact]
    public async Task RemoteAgent_ProvidesThumbnailWhenServiceCannotReadOriginalPath()
    {
        var image = fixture.Db.GetImages(fixture.SourceId).First();
        var bytes = await File.ReadAllBytesAsync(image.Path);
        var originalPath = image.Path;
        var hiddenPath = originalPath + ".agent-only";
        File.Move(originalPath, hiddenPath);
        try
        {
            using var agent = new ClientWebSocket();
            agent.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            agent.Options.SetRequestHeader("Authorization", "Bearer picer-test-token");
            var serviceUri = new UriBuilder(new Uri(fixture.BaseUrl)) { Scheme = "wss" }.Uri;
            await agent.ConnectAsync(new Uri(serviceUri, "api/agent/v1/connect"), CancellationToken.None);
            var agentId = "thumb-agent-" + Guid.NewGuid().ToString("N");
            await SendWsAsync(agent, new { type = "agent.register", agentId });
            _ = await ReceiveWsAsync(agent);
            var responseTask = Task.Run(async () =>
            {
                var request = JsonDocument.Parse(await ReceiveAgentRequestAsync(agent)).RootElement;
                await SendWsAsync(agent, new { type = "asset.response", requestId = request.GetProperty("requestId").GetString(), contentType = "image/png", contentBase64 = Convert.ToBase64String(bytes) });
            });
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl) };
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "picer-test-token");
            using var response = await client.GetAsync($"api/gateway/v1/thumbs/{image.Id}");
            response.EnsureSuccessStatusCode();
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
            await responseTask;
        }
        finally { File.Move(hiddenPath, originalPath); }
    }

    private static async Task SendWsAsync(ClientWebSocket socket, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveAgentRequestAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var message = await ReceiveWsAsync(socket, timeout.Token);
            using var document = JsonDocument.Parse(message);
            // The shared service fixture can already have persisted configuration.
            // Real agents apply this registration replay before handling requests.
            if (document.RootElement.GetProperty("type").GetString() == "agent.config") continue;
            return message;
        }
    }

    private static async Task<string> ReceiveWsAsync(ClientWebSocket socket, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Agent connection closed");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Search_AppliesSourceFilter(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        var matching = await gateway.SearchAsync(new SearchFilters { IncludeMissing = false, SourceId = fixture.SourceId }, pageSize: 2);
        Assert.Equal(5, matching.TotalCount);
        Assert.True(matching.Items.Count <= 2);
        var none = await gateway.SearchAsync(new SearchFilters { IncludeMissing = false, SourceId = 99_999_999 });
        Assert.Equal(0, none.TotalCount);
        Assert.Empty(none.Items);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Search_SortByFileName_OrdersThroughStringCursor(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        var filters = new SearchFilters { IncludeMissing = false, SourceId = fixture.SourceId, SortBy = SortField.FileName, SortDir = SortDirection.Ascending };
        var paths = new List<string>();
        string? cursor = null;
        do
        {
            var page = await gateway.SearchAsync(filters, pageSize: 2, cursor: cursor);
            paths.AddRange(page.Items.Select(item => item.Path));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(5, paths.Count);
        var sorted = paths.OrderBy(path => path, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, paths);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Search_SortByFileSizeDescending_OrdersThroughLongCursor(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        var filters = new SearchFilters { IncludeMissing = false, SourceId = fixture.SourceId, SortBy = SortField.FileSize, SortDir = SortDirection.Descending };
        var sizes = new List<long>();
        string? cursor = null;
        do
        {
            var page = await gateway.SearchAsync(filters, pageSize: 3, cursor: cursor);
            sizes.AddRange(page.Items.Select(item => item.FileSize));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(5, sizes.Count);
        Assert.Equal(sizes.OrderByDescending(size => size), sizes);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task ListImages_MissingRecordsExcludedUntilRequested(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        var first = (await gateway.ListImagesAsync(sourceId: fixture.SourceId, includeMissing: true, pageSize: 1)).Items.Single();
        fixture.MarkImageMissing(first.Id);
        try
        {
            var excluded = await gateway.ListImagesAsync(sourceId: fixture.SourceId, includeMissing: false, pageSize: 200);
            Assert.Equal(4, excluded.TotalCount);
            Assert.DoesNotContain(excluded.Items, item => item.Id == first.Id);

            var included = await gateway.ListImagesAsync(sourceId: fixture.SourceId, includeMissing: true, pageSize: 200);
            Assert.Equal(5, included.TotalCount);
            Assert.Contains(included.Items, item => item.Id == first.Id);
            Assert.Equal(ImageStatus.Missing, included.Items.Single(item => item.Id == first.Id).Status);
        }
        finally
        {
            fixture.MarkImageActive(first.Id);
        }
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Thumbnail_MissingImageReturnsNull(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        Assert.Null(await gateway.OpenThumbnailAsync(99_999_999));
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task ListImages_OversizedPageSizeSucceeds(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        var page = await gateway.ListImagesAsync(sourceId: fixture.SourceId, includeMissing: false, pageSize: 100_000);
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(5, page.Items.Count);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Backup_CreatesReadableDatabaseCopy(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        var destination = Path.Combine(fixture.Root + "-backup", $"backup-{transport}.db");
        var result = await gateway.BackupAsync(destination);
        Assert.Equal(Path.GetFullPath(destination), result);
        Assert.True(File.Exists(result));
        Assert.True(new FileInfo(result).Length > 0);
    }

    [Fact]
    public async Task Api_RequiresBearerToken()
    {
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        using var client = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseUrl + "/") };
        using var response = await client.GetAsync("api/gateway/v1/images");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_RejectsWrongBearerToken()
    {
        using var gateway = new HttpIndexGateway(fixture.BaseUrl, "wrong-token", fixture.SpkiPin);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.ListImagesAsync());
        Assert.Contains("401", exception.Message);
    }

    [Fact]
    public async Task Pipe_RejectsWrongBearerToken()
    {
        using var gateway = new PipeIndexGateway(fixture.PipeName, "wrong-token");
        await Assert.ThrowsAnyAsync<Exception>(() => gateway.ListImagesAsync());
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Sources_RegisterScanAndPurge(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        var sourceRoot = fixture.Root + "-src2";
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllBytes(Path.Combine(sourceRoot, "extra.png"), MakePng(9));

        var registered = await gateway.RegisterSourceAsync(sourceRoot);
        Assert.True(registered.Id > 0);
        Assert.Contains(await gateway.ListSourcesAsync(), source => source.Id == registered.Id);

        var jobId = await gateway.StartScanAsync(registered.Id);
        var job = await WaitForJobAsync(gateway, jobId);
        Assert.Null(job.Error);

        var page = await gateway.ListImagesAsync(sourceId: registered.Id, includeMissing: true, pageSize: 10);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(Path.Combine(sourceRoot, "extra.png"), page.Items.Single().Path);

        Assert.True(await gateway.RemoveSourceAsync(registered.Id));
        Assert.DoesNotContain(await gateway.ListSourcesAsync(), source => source.Id == registered.Id);
        Assert.Equal(0, (await gateway.ListImagesAsync(sourceId: registered.Id, includeMissing: true)).TotalCount);
    }

    [Fact]
    public async Task Sources_RegisterUnknownPathRejected()
    {
        using var gateway = fixture.CreateGateway("http");
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => gateway.RegisterSourceAsync(fixture.Root + "-does-not-exist"));
        Assert.Contains("400", exception.Message);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task VerifiedChanges_RemovesPhysicalRecordKeepsContent(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        var sourceRoot = fixture.Root + "-recycled-" + transport;
        Directory.CreateDirectory(sourceRoot);
        var source = fixture.Db.AddSource(sourceRoot);
        try
        {
            var path = Path.Combine(sourceRoot, "recycled.png");
            File.WriteAllBytes(path, [0x50, 0x49, 0x43]);
            var record = fixture.Db.UpsertImage(new ImageRecord
            {
                SourceId = source.Id,
                Path = path,
                FileSize = 3,
                ModifiedUtc = DateTime.UtcNow,
                IndexedUtc = DateTime.UtcNow,
                Sha256 = $"verified-changes-sha-{transport}",
                Status = ImageStatus.Active
            });
            fixture.Db.UpsertContent(record.Sha256!, record.FileSize, 0, 0);

            var removed = await gateway.RemoveRecycledRecordsAsync([record.Id]);
            Assert.Equal(1, removed);
        Assert.Null(await gateway.GetImageAsync(record.Id));
        Assert.Equal("complete", fixture.Db.GetContentHashState(record.Sha256!));
        }
        finally
        {
            fixture.Db.RemoveSource(source.Id);
            try { Directory.Delete(sourceRoot, true); } catch { }
        }
    }

    private static async Task<ScanJobInfo> WaitForJobAsync(IIndexGateway gateway, string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var job = await gateway.GetScanJobAsync(jobId);
            if (job is { Done: true }) return job;
            await Task.Delay(200);
        }
        throw new TimeoutException("扫描任务未在时限内完成");
    }

    private static byte[] MakePng(byte seed = 9)
    {
        using var image = new RasterImage(2, 1, new RgbPixel(seed, seed, seed));
        using var encoded = new MemoryStream();
        image.SaveAsPng(encoded);
        return encoded.ToArray();
    }
}
