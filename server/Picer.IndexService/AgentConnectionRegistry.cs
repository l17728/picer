using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Picer.IndexService;

internal sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public async Task HandleAsync(HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        AgentSession? session = null;
        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var message = await ReceiveTextAsync(socket, buffer, context.RequestAborted);
                if (message is null) break;
                using var document = JsonDocument.Parse(message.Value);
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (type == "agent.register")
                {
                    var agentId = root.TryGetProperty("agentId", out var idElement) ? idElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(agentId)) continue;
                    session = new AgentSession(agentId, socket);
                    _sessions.AddOrUpdate(agentId, session, (_, old) => old.DisposeAndReplace(session));
                    await session.SendAsync(new { type = "agent.registered" }, context.RequestAborted);
                    continue;
                }
                if (session is null) continue;
                if (type == "agent.heartbeat")
                {
                    await session.SendAsync(new { type = "agent.heartbeatAck", utc = DateTime.UtcNow }, context.RequestAborted);
                }
                else if (type == "asset.response")
                {
                    session.Complete(root);
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally
        {
            if (session is not null) _sessions.TryRemove(new KeyValuePair<string, AgentSession>(session.AgentId, session));
            session?.Dispose();
        }
    }

    private static async Task<ReadOnlyMemory<byte>?> ReceiveTextAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return message.ToArray();
        }
    }

    public async Task<(string ContentType, byte[] Content)?> RequestAsync(string agentId, string path, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(agentId, out var session)) return null;
        return await session.RequestAsync(path, cancellationToken);
    }

    private sealed class AgentSession(string agentId, WebSocket socket) : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<(string, byte[])?>> _pending = new();
        public string AgentId { get; } = agentId;
        private WebSocket Socket { get; } = socket;

        public AgentSession DisposeAndReplace(AgentSession replacement)
        {
            Dispose();
            return replacement;
        }

        public async Task SendAsync(object payload, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await _sendLock.WaitAsync(cancellationToken);
            try { await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
            finally { _sendLock.Release(); }
        }

        public async Task<(string ContentType, byte[] Content)?> RequestAsync(string path, CancellationToken cancellationToken)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<(string, byte[])?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[requestId] = completion;
            try
            {
                await SendAsync(new { type = "asset.open", requestId, path }, cancellationToken);
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            finally { _pending.TryRemove(requestId, out _); }
        }

        public void Complete(JsonElement root)
        {
            if (!root.TryGetProperty("requestId", out var id) || !_pending.TryGetValue(id.GetString() ?? "", out var completion)) return;
            if (root.TryGetProperty("error", out var error)) { completion.TrySetException(new IOException(error.GetString())); return; }
            var content = Convert.FromBase64String(root.GetProperty("contentBase64").GetString() ?? "");
            completion.TrySetResult((root.GetProperty("contentType").GetString() ?? "application/octet-stream", content));
        }

        public void Dispose()
        {
            foreach (var pending in _pending.Values) pending.TrySetCanceled();
            _sendLock.Dispose();
        }
    }
}
