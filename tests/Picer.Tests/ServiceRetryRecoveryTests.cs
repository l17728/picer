using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Video;
using Picer.IndexService;

namespace Picer.Tests;

public sealed class ServiceRetryRecoveryTests
{
    [Fact]
    public async Task RetrySurvivesServiceRestartAndCompletedHistoryStaysCompleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-retry-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var first = IndexServiceHost.Build(root, 0, "test-token");
        var url = await IndexServiceHost.StartAsync(first.App);
        string id;
        try
        {
            using var database = new PicerDatabase(Path.Combine(root, "picer.db"));
            var source = database.AddSource(root);
            var videoPath = Path.Combine(root, "probe.mp4");
            File.WriteAllBytes(videoPath, [0, 1, 2]);
            var video = new VideoRepository(database).Upsert(source.Id, videoPath);
            using var client = CreateClient(url, first.Certificate.GetCertHashString());
            using var failed = await client.GetAsync($"api/service/v1/video-probe/{video.Id}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            id = failed.Headers.GetValues("X-Picer-Task-Id").Single();
        }
        finally { await first.App.StopAsync(); await first.App.DisposeAsync(); }
        // A completed task still has historical running events; startup must inspect its LAST state.
        var finished = Guid.NewGuid().ToString("N");
        File.AppendAllLines(Path.Combine(root, "service-tasks.jsonl"), [
            JsonSerializer.Serialize(new { requestId = finished, operation = "probe", state = "running" }),
            JsonSerializer.Serialize(new { requestId = finished, operation = "probe", state = "succeeded" })]);
        var second = IndexServiceHost.Build(root, 0, "test-token");
        url = await IndexServiceHost.StartAsync(second.App);
        try
        {
            using var agent = new ClientWebSocket();
            agent.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == second.Certificate.GetCertHashString();
            agent.Options.SetRequestHeader("Authorization", "Bearer test-token");
            await agent.ConnectAsync(new Uri(new UriBuilder(url) { Scheme = "wss" }.Uri, "api/agent/v1/connect"), CancellationToken.None);
            await Send(agent, new { type = "agent.register", agentId = "recovered", capabilities = new[] { "ffmpeg.probe" } });
            await Receive(agent);
            using var client = CreateClient(url, second.Certificate.GetCertHashString());
            var retry = client.PostAsync($"api/service/v1/tasks/{id}/retry", null);
            var task = await Receive(agent);
            Assert.Equal("ffmpeg.probe", task.GetProperty("task").GetString());
            await Send(agent, new { type = "task.result", requestId = task.GetProperty("requestId").GetString(), payload = "{\"duration\":1}" });
            using var result = await retry;
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            using var persisted = await client.GetAsync($"api/service/v1/tasks/{id}/result");
            Assert.Equal(HttpStatusCode.OK, persisted.StatusCode);
            using var history = JsonDocument.Parse(await client.GetStringAsync("api/service/v1/tasks"));
            Assert.Equal("succeeded", history.RootElement.EnumerateArray().Last(e => e.GetProperty("requestId").GetString() == finished).GetProperty("state").GetString());
            Assert.Equal("succeeded", history.RootElement.EnumerateArray().Last(e => e.GetProperty("requestId").GetString() == id).GetProperty("state").GetString());
        }
        finally
        {
            await second.App.StopAsync(); await second.App.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static HttpClient CreateClient(string url, string thumbprint)
    {
        var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == thumbprint })
            { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        return client;
    }
    private static Task Send(ClientWebSocket socket, object data) => socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data)), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Receive(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        WebSocketReceiveResult chunk;
        do { chunk = await socket.ReceiveAsync(bytes, timeout.Token); buffer.Write(bytes, 0, chunk.Count); } while (!chunk.EndOfMessage);
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}
