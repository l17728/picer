using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Picer.Core.People;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class TrainingRelayTests
{
    [Theory]
    [InlineData("GET", "algorithms", true)]
    [InlineData("GET", "runtime", true)]
    [InlineData("GET", "model-rankings", true)]
    [InlineData("GET", "model-rankings/rounds", true)]
    [InlineData("GET", "runtime?url=http://other", false)]
    [InlineData("GET", "archive?limit=1000", true)]
    [InlineData("POST", "artifacts/abc-123/applied", true)]
    [InlineData("GET", "../config", false)]
    [InlineData("POST", "http://localhost/admin", false)]
    [InlineData("GET", "archive?limit=20&url=other", false)]
    [InlineData("DELETE", "jobs/test", false)]
    public void OnlyTrainingRoutesAreAllowed(string method, string path, bool expected) =>
        Assert.Equal(expected, TrainingRelayContract.IsAllowed(method, path));

    [Fact]
    public async Task TrainingClientUsesAuthenticatedServiceAndPreservesAgentHttpStatus()
    {
        using var fixture = new GatewayServiceFixture();
        using var agent = await Connect(fixture);
        using var client = Client(fixture);
        client.BaseAddress = new Uri(fixture.BaseUrl.TrimEnd('/') + "/api/service/v1/training/");
        using var training = new FaceTrainingSubmissionService(client);
        var response = training.GetAvailableAlgorithmsAsync();
        var task = await ReceiveTask(agent);
        Assert.Equal("training.proxy", task.GetProperty("task").GetString());
        var envelope = JsonSerializer.Deserialize<TrainingRelayRequest>(task.GetProperty("payload").GetString()!)!;
        Assert.Equal("GET", envelope.Method);
        Assert.Equal("algorithms", envelope.Path);
        await Send(agent, new { type = "task.result", requestId = task.GetProperty("requestId").GetString(),
            payload = JsonSerializer.Serialize(new TrainingRelayResponse(200, "[{\"id\":\"test\",\"modelVersion\":\"sface\",\"name\":\"test\",\"available\":true,\"embeddingVersion\":\"sface\"}]")) });
        Assert.True(Assert.Single(await response).Available);
        using var raw = Client(fixture);
        var denied = raw.GetAsync("api/service/v1/training/api/face-training/signing-key");
        task = await ReceiveTask(agent);
        await Send(agent, new { type = "task.result", requestId = task.GetProperty("requestId").GetString(),
            payload = JsonSerializer.Serialize(new TrainingRelayResponse(401, "{\"error\":\"unauthorized\"}")) });
        using var result = await denied;
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task FailedVideoRetryExecutesAndIndexesInsteadOfOnlyLoggingRequest()
    {
        using var fixture = new GatewayServiceFixture();
        var path = Path.Combine(fixture.Root, "retry.mp4");
        File.WriteAllBytes(path, [1, 2, 3]);
        var videos = new VideoRepository(fixture.Db);
        var video = videos.Upsert(fixture.SourceId, path);
        videos.SaveKeyframes(video.Id, [new VideoKeyframe(1, path, 0)]);
        var keyframe = Assert.Single(videos.GetKeyframes(video.Id, existingOnly: false));
        using var agent = await Connect(fixture);
        using var client = Client(fixture);
        var original = client.PostAsync($"api/service/v1/video-people/{video.Id}", null);
        var task = await ReceiveTask(agent);
        await Send(agent, new { type = "task.error", requestId = task.GetProperty("requestId").GetString(), message = "temporary_failure" });
        using var failed = await original;
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        var id = failed.Headers.GetValues("X-Picer-Task-Id").Single();
        var retry = client.PostAsync($"api/service/v1/tasks/{id}/retry", null);
        task = await ReceiveTask(agent);
        using var duplicate = await client.PostAsync($"api/service/v1/tasks/{id}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var faceResult = new FaceServiceResult(FaceServiceContract.CurrentSchema, "video.people", "insightface-arcface",
            [new FaceServiceFace(0, 1, .1f, .1f, .3f, .3f, .99f, [1f, 0f], keyframe.Id)]);
        await Send(agent, new { type = "task.result", requestId = task.GetProperty("requestId").GetString(), payload = JsonSerializer.Serialize(faceResult) });
        using var completed = await retry;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        using var command = fixture.Db.Command("SELECT person_analysis_status FROM videos WHERE id=$id");
        command.Parameters.AddWithValue("$id", video.Id);
        Assert.Equal("completed", command.ExecuteScalar());
        using var saved = await client.GetAsync($"api/service/v1/tasks/{id}/result");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(await completed.Content.ReadAsStringAsync(), await saved.Content.ReadAsStringAsync());
        using var repeated = await client.PostAsync($"api/service/v1/tasks/{id}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
    }

    private static HttpClient Client(GatewayServiceFixture fixture)
    {
        var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            cert is not null && Picer.Core.Gateway.HttpIndexGateway.ComputeSpkiPin(cert).SequenceEqual(fixture.SpkiPin) })
        { BaseAddress = new Uri(fixture.BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "picer-test-token");
        return client;
    }
    private static async Task<ClientWebSocket> Connect(GatewayServiceFixture fixture)
    {
        var agent = new ClientWebSocket();
        agent.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null &&
            Picer.Core.Gateway.HttpIndexGateway.ComputeSpkiPin(new System.Security.Cryptography.X509Certificates.X509Certificate2(cert)).SequenceEqual(fixture.SpkiPin);
        agent.Options.SetRequestHeader("Authorization", "Bearer picer-test-token");
        await agent.ConnectAsync(new Uri(new UriBuilder(fixture.BaseUrl) { Scheme = "wss" }.Uri, "api/agent/v1/connect"), CancellationToken.None);
        await Send(agent, new { type = "agent.register", agentId = "p1-test", capabilities = new[] { "training.proxy", "video.people" } });
        await Receive(agent);
        return agent;
    }
    private static Task Send(ClientWebSocket socket, object value) => socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> ReceiveTask(ClientWebSocket socket)
    {
        while (true) { var message = await Receive(socket); if (message.GetProperty("type").GetString() == "task.open") return message; }
    }
    private static async Task<JsonElement> Receive(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(chunk, timeout.Token); buffer.Write(chunk, 0, result.Count); } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}
