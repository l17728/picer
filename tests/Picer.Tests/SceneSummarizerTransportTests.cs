using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class SceneSummarizerTransportTests
{
    [Theory]
    [InlineData(false, "https://index.test/spaces/example")]
    [InlineData(true, "https://index.test/spaces/example/")]
    public async Task UsesConfiguredModelAndScopedAiRouteWithOrderedText(bool streaming, string serviceUrl)
    {
        var answer = JsonSerializer.Serialize(new SceneSummaryResult("测试场景", "测试摘要", ["测试"]));
        using var handler = new Handler(async request =>
        {
            Assert.Equal("/spaces/example/api/ai/v1/chat/stream", request.RequestUri!.AbsolutePath);
            Assert.Equal("device-session", request.Headers.Authorization?.Parameter);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("configured-scene-model", payload.RootElement.GetProperty("model").GetString());
            Assert.False(payload.RootElement.TryGetProperty("operation", out _));
            var messages = payload.RootElement.GetProperty("messages");
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            var text = messages[1].GetProperty("content").GetString()!;
            var ordered = text[text.IndexOf('\n')..].Trim();
            var descriptions = JsonSerializer.Deserialize<SceneRepresentativeDescription[]>(ordered)!;
            Assert.Equal([1, 2], descriptions.Select(item => item.Order));
            return JsonResponse(streaming
                ? "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = answer } } } }) + "\n\ndata: [DONE]\n\n"
                : JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "```json\n" + answer + "\n```" } } } }),
                streaming ? "text/event-stream" : "application/json");
        });
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "device-session");
        using var summarizer = new ServiceSceneGroupSummarizer(new Uri(serviceUrl), "", "configured-scene-model", client);

        var result = await summarizer.SummarizeAsync([
            new(2, DateTime.UnixEpoch.AddHours(1), "second", [], [], []),
            new(1, DateTime.UnixEpoch, "first", [], [], [])]);

        Assert.Equal("测试场景", result.SceneName);
        Assert.Equal("configured-scene-model", summarizer.ModelVersion);
        Assert.Equal(["测试"], result.SearchTerms);
    }

    [Fact]
    public async Task RejectsStreamFailureEvenAfterACompleteLookingSummary()
    {
        var answer = JsonSerializer.Serialize(new SceneSummaryResult("不应保存", "未完成", []));
        using var handler = new Handler(_ => Task.FromResult(JsonResponse(
            "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = answer } } } }) +
            "\n\nevent: error\ndata: {\"error\":\"ai_proxy_failed\"}\n\n", "text/event-stream")));
        using var client = new HttpClient(handler);
        using var summarizer = new ServiceSceneGroupSummarizer(new Uri("https://index.test/"), "token", "model", client);

        var failure = await Assert.ThrowsAsync<VisionStreamException>(() => summarizer.SummarizeAsync([
            new(1, DateTime.UnixEpoch, "fixture", [], [], [])]));
        Assert.Equal("ai_proxy_failed", failure.ErrorCode);
    }

    [Fact]
    public async Task PreservesHttp503AndDoesNotReplayModelRequests()
    {
        var calls = 0;
        using var handler = new Handler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using var client = new HttpClient(handler);
        using var summarizer = new ServiceSceneGroupSummarizer(new Uri("https://index.test/"), "token", "model", client);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => summarizer.SummarizeAsync([
            new(1, DateTime.UnixEpoch, "fixture", [], [], [])]));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Equal(1, calls);
    }

    private static HttpResponseMessage JsonResponse(string text, string mediaType = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, mediaType) };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
