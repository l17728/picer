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

    [Fact]
    public async Task PlanAsync_OpensParentLocationAndListsChildren()
    {
        using var chat = Chat("invalid");
        var agent = new VoiceCommandAgent(chat);
        var result = await agent.PlanAsync("打开上海的照片", ["地球/中国/上海", "地球/中国/上海/浦东新区", "地球/中国/上海/徐汇区"], []);
        Assert.Equal(VoiceActionKind.OpenLocation, result.Kind);
        Assert.Equal("地球/中国/上海", result.Target);
        Assert.Contains("浦东新区", result.Reply);
    }

    [Fact]
    public async Task PlanAsync_ConfirmationReusesPreviousLocation()
    {
        using var chat = Chat("invalid");
        var agent = new VoiceCommandAgent(chat);
        await agent.PlanAsync("打开上海的照片", ["地球/中国/上海"], []);
        var result = await agent.PlanAsync("对", ["地球/中国/上海"], []);
        Assert.Equal(VoiceActionKind.OpenLocation, result.Kind);
        Assert.Equal("地球/中国/上海", result.Target);
    }

    [Fact]
    public async Task PlanAsync_ResolvesAsrLocationVariantFromPendingChildren()
    {
        using var chat = Chat("invalid");
        var agent = new VoiceCommandAgent(chat);
        var paths = new[] { "地球/日本", "地球/日本/佐贺县", "地球/日本/长崎县" };
        await agent.PlanAsync("打开日本的照片", paths, []);
        var result = await agent.PlanAsync("长期限", paths, []);
        Assert.Equal(VoiceActionKind.OpenLocation, result.Kind);
        Assert.Equal("地球/日本/长崎县", result.Target);
    }

    [Fact]
    public async Task PlanAsync_MergesLocationRequestAfterDirectoryTurn()
    {
        using var chat = Chat("invalid");
        var agent = new VoiceCommandAgent(chat);
        var paths = new[] { "地球/日本", "地球/日本/佐贺县", "地球/日本/长崎县" };
        var first = await agent.PlanAsync("打开目录浏览", paths, []);
        Assert.Equal(VoiceActionKind.OpenModule, first.Kind);
        var second = await agent.PlanAsync("看看日本的照片", paths, []);
        Assert.Equal(VoiceActionKind.OpenLocation, second.Kind);
        Assert.Equal("地球/日本", second.Target);
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
