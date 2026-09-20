using System.Net;
using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Indexing;
using Picer.Core.Logging;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Thumbnails;
using Picer.IndexService;

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
        using (var image = new Image<Rgb24>(2, 1, Color.White))
        using (var encoded = new MemoryStream())
        {
            image.Save(encoded, new PngEncoder());
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
        var image = Image.Load(bytes.ToArray());
        Assert.True(image.Width > 0);
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
            var request = JsonDocument.Parse(await ReceiveWsAsync(agent)).RootElement;
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
                var request = JsonDocument.Parse(await ReceiveWsAsync(agent)).RootElement;
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

    private static async Task<string> ReceiveWsAsync(ClientWebSocket socket)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
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
        using var image = new Image<Rgb24>(2, 1, new Rgb24(seed, seed, seed));
        using var encoded = new MemoryStream();
        image.Save(encoded, new PngEncoder());
        return encoded.ToArray();
    }
}
