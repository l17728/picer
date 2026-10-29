using Picer.Core.Search;
using Picer.Core.Vision;

namespace Picer.E2E;

public sealed class ModelServerVoiceE2ETests
{
    [Fact]
    [Trait("Category", "RuntimeComponent")]
    public async Task VoiceAgent_UsesConfiguredVlModel_EndToEnd()
    {
        if (Environment.GetEnvironmentVariable("PICER_RUN_REAL_MODEL_E2E") != "1")
            return; // CI stays offline; enable explicitly on a machine with ModelServer running.

        using var chat = new OpenAiCompatibleChatClient(
            new Uri(Environment.GetEnvironmentVariable("PICER_REAL_MODEL_ENDPOINT") ?? "http://127.0.0.1:8000/v1/chat/completions"),
            Environment.GetEnvironmentVariable("PICER_REAL_MODEL_API_KEY") ?? "local-qwen-key",
            Environment.GetEnvironmentVariable("PICER_REAL_MODEL_NAME") ?? "Qwen/Qwen3-VL-8B-Instruct-GGUF:Q4_K_M");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var reply = await chat.CompleteAsync("只输出JSON。", "回复 {\"ok\":true}", 32, timeout.Token);
        Assert.Contains("ok", reply, StringComparison.OrdinalIgnoreCase);

        var agent = new VoiceCommandAgent(chat);
        var action = await agent.PlanAsync("打开目录浏览，看看日本的照片", new[] { "地球/日本" }, Array.Empty<string>(), timeout.Token);
        Assert.Equal(VoiceActionKind.OpenLocation, action.Kind);
        Assert.Equal("地球/日本", action.Target);
    }
}
