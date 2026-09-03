using System.Net;
using System.Text;
using Picer.Core.Search;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class VoiceCommandAgentTests
{
    [Fact]
    public async Task PlanAsync_AllowsOnlyExistingLocationPath()
    {
        using var chat = Chat("{\"action\":\"open_location\",\"reply\":\"已打开\",\"target\":\"地球/日本\",\"query\":null,\"includeVideos\":false}");
        var result = await new VoiceCommandAgent(chat).PlanAsync("看看日本", ["地球/日本"], []);
        Assert.Equal(VoiceActionKind.OpenLocation, result.Kind);
        Assert.Equal("地球/日本", result.Target);
    }

    [Fact]
    public async Task PlanAsync_RejectsHallucinatedLocation()
    {
        using var chat = Chat("{\"action\":\"open_location\",\"reply\":\"已打开\",\"target\":\"地球/火星\",\"query\":null,\"includeVideos\":false}");
        var result = await new VoiceCommandAgent(chat).PlanAsync("看看火星", ["地球/日本"], []);
        Assert.Equal(VoiceActionKind.Clarify, result.Kind);
    }

    [Fact]
    public async Task PlanAsync_EmptySpeechDoesNotCallModel()
    {
        using var chat = Chat("invalid");
        var result = await new VoiceCommandAgent(chat).PlanAsync(" ", [], []);
        Assert.Equal(VoiceActionKind.Clarify, result.Kind);
    }

    private static OpenAiCompatibleChatClient Chat(string response)
    {
        var handler = new ReplyHandler(response);
        return new OpenAiCompatibleChatClient(new Uri("http://127.0.0.1/chat"), "", "test", new HttpClient(handler));
    }

    private sealed class ReplyHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content = response } } }
                }), Encoding.UTF8, "application/json")
            });
    }
}
