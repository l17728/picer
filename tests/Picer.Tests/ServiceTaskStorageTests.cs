using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Picer.IndexService;

namespace Picer.Tests;

public sealed class ServiceTaskStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-task-storage-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create();
        return new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET"; context.Request.Path = "/api/service/v1/video-audio/1";
        context.Response.Body = new MemoryStream(); context.Response.ContentType = "audio/wav";
        return context;
    }

    [Fact]
    public async Task ResponseIsForwardedBeforeCompletionAndReplayUsesBinaryFile()
    {
        var journal = new TaskJournal(_root);
        using var certificate = Certificate();
        using var runner = new ServiceTaskRunner(_root, journal, certificate, "test", () => new Uri("https://localhost"));
        var context = Context(); var live = (MemoryStream)context.Response.Body;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = runner.InvokeAsync(context, async () =>
        {
            await context.Response.Body.WriteAsync(new byte[] { 1, 2, 3 });
            started.SetResult(); await finish.Task;
            await context.Response.Body.WriteAsync(new byte[] { 4, 5 });
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, live.Length); Assert.False(running.IsCompleted);
        finish.SetResult(); await running;
        var id = context.Response.Headers["X-Picer-Task-Id"].ToString();
        Assert.Equal(5, new FileInfo(Path.Combine(_root, "task-requests", id + ".body")).Length);
        Assert.False(File.Exists(Path.Combine(_root, "task-requests", id + ".result.json")));
        var replay = Context(); await runner.GetResult(id).ExecuteAsync(replay);
        Assert.Equal(live.ToArray(), ((MemoryStream)replay.Response.Body).ToArray());
        Assert.Equal("succeeded", Assert.Single(journal.Read()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task PerResultQuotaDoesNotTurnSuccessfulLiveResponseIntoFailure()
    {
        var journal = new TaskJournal(_root);
        using var certificate = Certificate();
        using var runner = new ServiceTaskRunner(_root, journal, certificate, "test", () => new Uri("https://localhost"), new(MaximumResultBytes: 4, MaximumTotalBytes: 65536));
        var context = Context(); var live = (MemoryStream)context.Response.Body;
        await runner.InvokeAsync(context, () => context.Response.Body.WriteAsync(new byte[16]).AsTask());
        Assert.Equal(200, context.Response.StatusCode); Assert.Equal(16, live.Length);
        var state = Assert.Single(journal.Read());
        Assert.Equal("succeeded", state.GetProperty("state").GetString());
        Assert.Equal("result_storage_item_limit", state.GetProperty("error").GetString());
        var id = context.Response.Headers["X-Picer-Task-Id"].ToString();
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(runner.GetResult(id)).StatusCode);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "task-requests"), "*.part"));
    }

    [Fact]
    public async Task TaskCountAndTotalStorageStayBounded()
    {
        var journal = new TaskJournal(_root);
        using var certificate = Certificate();
        using var runner = new ServiceTaskRunner(_root, journal, certificate, "test", () => new Uri("https://localhost"), new(MaximumResultBytes: 1024, MaximumTotalBytes: 65536, MaximumTasks: 2));
        for (var index = 0; index < 8; index++)
        {
            var context = Context();
            await runner.InvokeAsync(context, () => context.Response.Body.WriteAsync(new byte[512]).AsTask());
        }
        var files = Directory.EnumerateFiles(Path.Combine(_root, "task-requests")).ToArray();
        Assert.True(files.Count(path => path.EndsWith(".body")) <= 2);
        Assert.True(files.Sum(path => new FileInfo(path).Length) <= 65536);
        Assert.Equal(8, journal.Read().Count);
    }

    [Fact]
    public async Task MetadataPersistenceFailureDoesNotReplaceSuccessfulResponse()
    {
        var journal = new TaskJournal(_root);
        using var certificate = Certificate();
        using var runner = new ServiceTaskRunner(_root, journal, certificate, "test", () => new Uri("https://localhost"));
        var context = Context(); var live = (MemoryStream)context.Response.Body;
        await runner.InvokeAsync(context, async () =>
        {
            await context.Response.Body.WriteAsync(new byte[] { 1, 2, 3 });
            var id = context.Response.Headers["X-Picer-Task-Id"].ToString();
            Directory.CreateDirectory(Path.Combine(_root, "task-requests", id + ".result-meta.json.tmp"));
        });
        Assert.Equal(200, context.Response.StatusCode); Assert.Equal(3, live.Length);
        var state = Assert.Single(journal.Read());
        Assert.Equal("succeeded", state.GetProperty("state").GetString());
        Assert.Equal("result_storage_unavailable", state.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ExpiredReplayIsRemovedWithoutChangingFinalTaskState()
    {
        var journal = new TaskJournal(_root);
        using var certificate = Certificate();
        using var runner = new ServiceTaskRunner(_root, journal, certificate, "test", () => new Uri("https://localhost"), new(ResultLifetime: TimeSpan.FromMilliseconds(20)));
        var context = Context();
        await runner.InvokeAsync(context, () => context.Response.Body.WriteAsync(new byte[] { 1 }).AsTask());
        await Task.Delay(60);
        var id = context.Response.Headers["X-Picer-Task-Id"].ToString();
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(runner.GetResult(id)).StatusCode);
        Assert.Equal("succeeded", Assert.Single(journal.Read()).GetProperty("state").GetString());
    }

    [Fact]
    public void LegacyImportIsIncrementalAndRestartPreservesFinalStates()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "service-tasks.jsonl");
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllLines(path, [JsonSerializer.Serialize(new { requestId = id, operation = "probe", state = "running" }),
            JsonSerializer.Serialize(new { requestId = id, operation = "probe", state = "failed" })]);
        var first = new TaskJournal(_root);
        Assert.Equal(0, new FileInfo(path).Length);
        Assert.True(first.RequestRetry(id)); Assert.False(first.RequestRetry(id));
        first.Append(id, "probe", "succeeded");
        var secondId = Guid.NewGuid().ToString("N");
        File.AppendAllLines(path, ["damaged-json", JsonSerializer.Serialize(new { requestId = secondId, operation = "probe", state = "running" })]);
        var restarted = new TaskJournal(_root); restarted.MarkInterrupted();
        Assert.Equal(0, new FileInfo(path).Length);
        var states = restarted.Read().ToDictionary(row => row.GetProperty("requestId").GetString()!, row => row.GetProperty("state").GetString());
        Assert.Equal("succeeded", states[id]); Assert.Equal("abandoned", states[secondId]);
        Assert.True(restarted.RequestRetry(secondId)); Assert.False(restarted.RequestRetry(id));
    }

    [Fact]
    public async Task VideoPurgeRemovesCompletedReplayPayloadResultAndJournalAcrossRestart()
    {
        var journal = new TaskJournal(_root);
        using var certificate = Certificate();
        string deletedId;
        using (var runner = new ServiceTaskRunner(_root, journal, certificate, "test", () => new Uri("https://localhost")))
        {
            var deleted = Context(); var other = Context(); other.Request.Path = "/api/service/v1/video-audio/2";
            await runner.InvokeAsync(deleted, () => deleted.Response.Body.WriteAsync(new byte[] { 1, 2 }).AsTask());
            await runner.InvokeAsync(other, () => other.Response.Body.WriteAsync(new byte[] { 3 }).AsTask());
            deletedId = deleted.Response.Headers["X-Picer-Task-Id"].ToString();
            Assert.True(await runner.PurgeVideoAsync(1, _ => Task.FromResult(true), CancellationToken.None));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "task-requests"), deletedId + ".*"));
            Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(runner.GetResult(deletedId)).StatusCode);
            Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await runner.RetryAsync(deletedId, CancellationToken.None)).StatusCode);
            Assert.Single(journal.Read());
            var replay = Context(); await runner.GetResult(other.Response.Headers["X-Picer-Task-Id"].ToString()).ExecuteAsync(replay);
            Assert.Equal(new byte[] { 3 }, ((MemoryStream)replay.Response.Body).ToArray());
        }
        using var restarted = new ServiceTaskRunner(_root, new TaskJournal(_root), certificate, "test", () => new Uri("https://localhost"));
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await restarted.RetryAsync(deletedId, CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task VideoPurgeWaitsForCanceledHandlerAndErasesItsLateResult()
    {
        var journal = new TaskJournal(_root);
        using var certificate = Certificate();
        using var runner = new ServiceTaskRunner(_root, journal, certificate, "test", () => new Uri("https://localhost"));
        var context = Context();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = runner.InvokeAsync(context, async () =>
        {
            started.SetResult(); await finish.Task;
            Assert.True(context.RequestAborted.IsCancellationRequested);
            await context.Response.Body.WriteAsync(new byte[] { 9 });
        });
        await started.Task;
        var purged = false;
        var purge = runner.PurgeVideoAsync(1, _ => { purged = true; return Task.FromResult(true); }, CancellationToken.None);
        Assert.False(purge.IsCompleted); Assert.False(purged);
        var blocked = Context();
        await runner.InvokeAsync(blocked, () => throw new InvalidOperationException("must_not_run"));
        Assert.Equal(409, blocked.Response.StatusCode);
        finish.SetResult(); await running; Assert.True(await purge);
        Assert.Empty(journal.Read()); Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "task-requests")));
    }
}
