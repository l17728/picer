using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Logging;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Thumbnails;
using Picer.IndexService;
using Xunit.Abstractions;

namespace Picer.Tests;

public sealed class GatewayPerfFixture : IDisposable
{
    private const string BearerToken = "picer-perf-token";
    private readonly string _root;
    public string BaseUrl { get; }
    public byte[] SpkiPin { get; }
    public PicerDatabase Db { get; }
    public long SourceId { get; }
    private readonly WebApplication _app;

    public GatewayPerfFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), "picer-gw-perf-" + Guid.NewGuid().ToString("N"));
        var root = _root;
        Directory.CreateDirectory(root);
        AppLogger.Initialize(Path.Combine(root, "logs"));
        Db = new PicerDatabase(Path.Combine(root, "picer.db"));
        var source = Db.AddSource(root);
        SourceId = source.Id;

        var takenBase = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var transaction = Db.BeginTransaction())
        {
            for (var index = 0; index < 100_000; index++)
            {
                Db.UpsertImageInTransaction(new ImageRecord
                {
                    SourceId = SourceId,
                    Path = Path.Combine(root, $"img{index:D6}.png"),
                    FileSize = 1024 + index,
                    ModifiedUtc = takenBase.AddSeconds(index),
                    TakenUtc = takenBase.AddSeconds(index),
                    IndexedUtc = takenBase,
                    Width = 800,
                    Height = 600,
                    Status = ImageStatus.Active
                }, transaction);
            }
            transaction.Commit();
        }

        var instance = IndexServiceHost.Build(root, port: 0, bearerToken: BearerToken);
        _app = instance.App;
        BaseUrl = IndexServiceHost.StartAsync(_app).GetAwaiter().GetResult();
        SpkiPin = ServiceSecurity.ComputeSpkiPin(instance.Certificate);
    }

    public IIndexGateway CreateGateway(bool useHttp) =>
        useHttp ? new HttpIndexGateway(BaseUrl, BearerToken, SpkiPin) : new LocalGateway(Db, new ThumbnailService(Path.Combine(Path.GetTempPath(), "picer-gw-perf-thumbs")));

    public void Dispose()
    {
        _app.StopAsync().GetAwaiter().GetResult();
        _app.DisposeAsync().GetAwaiter().GetResult();
        Db.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }
}

[CollectionDefinition(nameof(GatewayPerformanceCollection), DisableParallelization = true)]
public sealed class GatewayPerformanceCollection { }

[Collection(nameof(GatewayPerformanceCollection))]
[Trait("Category", "Performance")]
public sealed class GatewayPerformanceTests(GatewayPerfFixture fixture, ITestOutputHelper output) : IClassFixture<GatewayPerfFixture>
{
    public static IEnumerable<object[]> Transports()
    {
        yield return ["local", false];
        yield return ["http", true];
    }

    private static double Percentile(List<double> samples, double percentile)
    {
        var sorted = samples.OrderBy(value => value).ToList();
        var position = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(position, 0, sorted.Count - 1)];
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task List_FirstPage200_MeetsLatencyBudget(string label, bool useHttp)
    {
        using var gateway = fixture.CreateGateway(useHttp);
        var samples = new List<double>();
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            var page = await gateway.ListImagesAsync(sourceId: fixture.SourceId, includeMissing: false, pageSize: 200);
            stopwatch.Stop();
            Assert.Equal(200, page.Items.Count);
            if (iteration >= 2) samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }
        Report(label, "list", samples);
        Assert.True(Percentile(samples, 95) < 1500, $"list p95 {Percentile(samples, 95):F1} ms exceeds budget");
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Search_FirstPage200_MeetsLatencyBudget(string label, bool useHttp)
    {
        using var gateway = fixture.CreateGateway(useHttp);
        var filters = new SearchFilters { IncludeMissing = false, SourceId = fixture.SourceId };
        var samples = new List<double>();
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            var page = await gateway.SearchAsync(filters, pageSize: 200);
            stopwatch.Stop();
            Assert.Equal(200, page.Items.Count);
            if (iteration >= 2) samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }
        Report(label, "search", samples);
        Assert.True(Percentile(samples, 95) < 1500, $"search p95 {Percentile(samples, 95):F1} ms exceeds budget");
    }

    private void Report(string label, string operation, List<double> samples) =>
        output.WriteLine($"[perf] {label} {operation} over 100k rows: p50={Percentile(samples, 50):F1} ms, p95={Percentile(samples, 95):F1} ms, max={samples.Max():F1} ms (n={samples.Count}, 预算 1500 ms)");
}
