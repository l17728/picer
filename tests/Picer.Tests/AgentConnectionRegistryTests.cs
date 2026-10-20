using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Picer.IndexService;

namespace Picer.Tests;

public sealed class AgentConnectionRegistryTests
{
    [Fact]
    public async Task StrictSpaceRoutesUnspecifiedTaskToItsOnlyCapableAgent()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-agent-task-" + Guid.NewGuid().ToString("N"));
        var registry = new AgentConnectionRegistry(new TaskJournal(root), strictRouting: true);
        var app = WebApplication.CreateBuilder().Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseWebSockets();
        app.Map("/agent", registry.HandleAsync);
        using var agent = new ClientWebSocket();
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            agent.Options.SetRequestHeader("X-Picer-Agent-Grant", "space-agent-1");
            await agent.ConnectAsync(new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), CancellationToken.None);
            await SendAsync(agent, """{"type":"agent.register","agentId":"ignored","capabilities":["ffmpeg.keyframes"]}""");
            using (var registered = await ReceiveJsonAsync(agent))
                Assert.Equal("agent.registered", registered.RootElement.GetProperty("type").GetString());

            var task = registry.RequestTaskAsync(null, "ffmpeg.keyframes", "video-payload", CancellationToken.None);
            using var request = await ReceiveJsonAsync(agent);
            Assert.Equal("task.open", request.RootElement.GetProperty("type").GetString());
            Assert.Equal("ffmpeg.keyframes", request.RootElement.GetProperty("task").GetString());
            var requestId = request.RootElement.GetProperty("requestId").GetString();
            await SendAsync(agent, JsonSerializer.Serialize(new { type = "task.result", requestId, payload = "frames-json" }));

            Assert.Equal("frames-json", await task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            agent.Abort();
            await app.StopAsync();
            await app.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task StrictSpaceRoutesAiToItsOnlyEligibleProxyWhenClientHasNoAgentId()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-agent-registry-" + Guid.NewGuid().ToString("N"));
        var registry = new AgentConnectionRegistry(new TaskJournal(root), strictRouting: true);
        var app = WebApplication.CreateBuilder().Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseWebSockets();
        app.Map("/agent", registry.HandleAsync);
        using var agent = new ClientWebSocket();
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            agent.Options.SetRequestHeader("X-Picer-Agent-Grant", "space-agent-1");
            await agent.ConnectAsync(new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"), CancellationToken.None);
            await SendAsync(agent, """{"type":"agent.register","agentId":"ignored","capabilities":["ai.proxy"]}""");
            using (var registered = await ReceiveJsonAsync(agent))
                Assert.Equal("agent.registered", registered.RootElement.GetProperty("type").GetString());

            var responseBytes = Encoding.UTF8.GetBytes("model-response");
            var received = new MemoryStream();
            var streamTask = registry.StreamAiAsync(null, Encoding.UTF8.GetBytes("request"), async chunk =>
            {
                await received.WriteAsync(chunk);
            }, CancellationToken.None);
            using var request = await ReceiveJsonAsync(agent);
            Assert.Equal("ai.open", request.RootElement.GetProperty("type").GetString());
            var requestId = request.RootElement.GetProperty("requestId").GetString();
            await SendAsync(agent, JsonSerializer.Serialize(new
            {
                type = "ai.chunk", requestId,
                contentBase64 = Convert.ToBase64String(responseBytes)
            }));
            await SendAsync(agent, JsonSerializer.Serialize(new { type = "ai.done", requestId }));
            await streamTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(responseBytes, received.ToArray());
        }
        finally
        {
            agent.Abort();
            await app.StopAsync();
            await app.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(chunk, CancellationToken.None);
            await buffer.WriteAsync(chunk.AsMemory(0, result.Count));
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(buffer.ToArray());
    }
}
