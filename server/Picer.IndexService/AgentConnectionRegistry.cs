using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Picer.Core.Logging;

namespace Picer.IndexService;

internal sealed partial class AgentConnectionRegistry
{
    private const int MaxTaskPayloadChars = 16 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskJournal _journal;
    private readonly object _configLock = new();
    private JsonElement? _currentConfig;
    public AgentConnectionRegistry(TaskJournal journal, JsonElement? currentConfig = null)
    {
        _journal = journal;
        _currentConfig = currentConfig?.Clone();
        _mediaExpiry = new(_ => SweepMedia(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }
    public int OnlineCount => _sessions.Count;
    public IReadOnlyList<string> OnlineCapabilities => _sessions.Values
        .SelectMany(session => session.Capabilities)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();

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
                    if (root.TryGetProperty("capabilities", out var capabilityElement) && capabilityElement.ValueKind == JsonValueKind.Array)
                        session.SetCapabilities(capabilityElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString()!).Where(value => !string.IsNullOrWhiteSpace(value)));
                    _sessions.AddOrUpdate(agentId, session, (_, old) => old.DisposeAndReplace(session));
                    await session.SendAsync(new { type = "agent.registered" }, context.RequestAborted);
                    JsonElement? currentConfig;
                    lock (_configLock) currentConfig = _currentConfig?.Clone();
                    if (currentConfig is { } config)
                    {
                        await session.SendAsync(new { type = "agent.config", config }, context.RequestAborted);
                        AppLogger.Info("MediaAgent service configuration replayed on registration");
                    }
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
                else if (type is "task.result" or "task.error")
                {
                    session.CompleteTask(root);
                }
                else if (type == "task.chunk")
                {
                    session.CompleteTaskChunk(root);
                }
                else if (type == "task.cancel")
                {
                    session.CancelTask(root);
                }
                else if (type == "agent.capabilities" && root.TryGetProperty("capabilities", out var updated) && updated.ValueKind == JsonValueKind.Array)
                {
                    session.SetCapabilities(updated.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(value => !string.IsNullOrWhiteSpace(value)));
                }
                else if (type is "ai.chunk" or "ai.done" or "ai.error")
                {
                    session.CompleteAi(root);
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally
        {
            if (session is not null)
            {
                _sessions.TryRemove(new KeyValuePair<string, AgentSession>(session.AgentId, session));
                ClearPooledMedia(owner: session);
            }
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

    public async Task<(string ContentType, byte[] Content)?> RequestAsync(string? agentId, string path, CancellationToken cancellationToken)
    {
        var session = await WaitForSessionAsync(agentId, cancellationToken);
        // A remote GUI does not necessarily know the source machine's agent id
        // (and a reconnect may replace the registered id).  This is a single-
        // tenant service, so safely fall back only when exactly one agent is
        // online; never guess when multiple source agents are connected.
        if (session is null && _sessions.Count == 1)
            session = _sessions.Values.Single();
        if (session is null) return null;
        return await session.RequestAsync(path, cancellationToken);
    }

    public async Task StreamAiAsync(string? agentId, ReadOnlyMemory<byte> payload,
        Func<ReadOnlyMemory<byte>, Task> onChunk, CancellationToken cancellationToken)
    {
        var session = await WaitForSessionAsync(agentId, cancellationToken);
        if (session is null) throw new InvalidOperationException("media_agent_offline");
        await session.StreamAiAsync(payload, onChunk, cancellationToken);
    }

    public async Task BroadcastConfigAsync(JsonElement config, CancellationToken cancellationToken = default)
    {
        var safe = config.Clone();
        lock (_configLock) _currentConfig = safe;
        foreach (var session in _sessions.Values)
        {
            try { await session.SendAsync(new { type = "agent.config", config = safe }, cancellationToken); }
            catch (Exception) { /* disconnected agents are removed by the receive loop */ }
        }
    }

    public async Task<string?> RequestTaskAsync(string? agentId, string task, string payload,
        CancellationToken cancellationToken)
    {
        var session = await WaitForSessionAsync(agentId, cancellationToken);
        if (session is null) return null;
        var requestId = Guid.NewGuid().ToString("N");
        _journal.Append(requestId, task, "queued");
        try
        {
            _journal.Append(requestId, task, "running");
            var result = await session.RequestTaskAsync(task, payload, cancellationToken);
            _journal.Append(requestId, task, "succeeded");
            return result;
        }
        catch (OperationCanceledException)
        {
            _journal.Append(requestId, task, "canceled");
            throw;
        }
        catch (Exception exception)
        {
            _journal.Append(requestId, task, "failed", exception.GetType().Name);
            throw;
        }
    }

    public async Task StreamTaskAsync(string? agentId, string task, string payload, Func<ReadOnlyMemory<byte>, Task> onChunk, CancellationToken cancellationToken)
    {
        var session = await WaitForSessionAsync(agentId, cancellationToken);
        if (session is null) throw new InvalidOperationException("media_agent_offline");
        await session.StreamTaskAsync(task, payload, onChunk, cancellationToken);
    }

    private async Task<AgentSession?> WaitForSessionAsync(string? agentId, CancellationToken cancellationToken)
    {
        // A reconnect normally completes within a few seconds. Waiting here
        // avoids turning a harmless WebSocket reconnect into an immediate 503.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var session = !string.IsNullOrWhiteSpace(agentId)
                ? _sessions.GetValueOrDefault(agentId)
                : _sessions.Values.FirstOrDefault();
            if (session is not null || (_sessions.Count > 1 && !string.IsNullOrWhiteSpace(agentId))) return session;
            if (attempt < 5) await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        return null;
    }

    private sealed class AgentSession(string agentId, WebSocket socket) : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<(string, byte[])?>> _pending = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _taskPending = new();
        private readonly ConcurrentDictionary<string, Channel<byte[]>> _taskStreams = new();
        private readonly ConcurrentDictionary<string, Channel<AiMessage>> _aiPending = new();
        public string AgentId { get; } = agentId;
        public IReadOnlyCollection<string> Capabilities => _capabilities;
        private readonly HashSet<string> _capabilities = new(StringComparer.OrdinalIgnoreCase);
        private WebSocket Socket { get; } = socket;

        public void SetCapabilities(IEnumerable<string> capabilities)
        {
            _capabilities.Clear();
            foreach (var capability in capabilities) _capabilities.Add(capability);
        }

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
                // A cold source (network volume or large original) may need more
                // than 30 seconds before a thumbnail can be generated. Keep the
                // wait bounded and honor caller disconnect/cancellation.
                return await completion.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
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

        public void CompleteTask(JsonElement root)
        {
            if (!root.TryGetProperty("requestId", out var id)) return;
            _taskStreams.TryGetValue(id.GetString() ?? "", out var stream);
            if (!_taskPending.TryGetValue(id.GetString() ?? "", out var completion)) { stream?.Writer.TryComplete(); return; }
            stream?.Writer.TryComplete();
            if (root.GetProperty("type").GetString() == "task.error")
            {
                completion.TrySetException(new IOException(root.TryGetProperty("message", out var message) ? message.GetString() : "task_error"));
                return;
            }
            completion.TrySetResult(root.TryGetProperty("payload", out var payload) ? payload.GetString() : null);
        }

        public void CompleteTaskChunk(JsonElement root)
        {
            if (!root.TryGetProperty("requestId", out var id) || !_taskStreams.TryGetValue(id.GetString() ?? "", out var channel)) return;
            if (root.TryGetProperty("contentBase64", out var content)) channel.Writer.TryWrite(Convert.FromBase64String(content.GetString() ?? ""));
        }

        public async Task StreamTaskAsync(string task, string payload, Func<ReadOnlyMemory<byte>, Task> onChunk, CancellationToken cancellationToken)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait });
            _taskStreams[requestId] = channel;
            try
            {
                await SendAsync(new { type = "task.open", requestId, task, payload, stream = true }, cancellationToken);
                await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken)) await onChunk(chunk);
            }
            finally
            {
                _taskStreams.TryRemove(requestId, out _);
                try { await SendAsync(new { type = "task.cancel", requestId }, CancellationToken.None); } catch { }
            }
        }

        public void CancelTask(JsonElement root)
        {
            if (root.TryGetProperty("requestId", out var id) && _taskPending.TryGetValue(id.GetString() ?? "", out var completion))
                completion.TrySetCanceled();
        }

        public async Task<string?> RequestTaskAsync(string task, string payload, CancellationToken cancellationToken)
        {
            if (payload.Length > MaxTaskPayloadChars) throw new InvalidDataException("task_payload_too_large");
            var requestId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _taskPending[requestId] = completion;
            try
            {
                await SendAsync(new { type = "task.open", requestId, task, payload }, cancellationToken);
                return await completion.Task.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { await SendAsync(new { type = "task.cancel", requestId }, CancellationToken.None); } catch { }
                throw;
            }
            finally { _taskPending.TryRemove(requestId, out _); }
        }

        public void CompleteAi(JsonElement root)
        {
            if (!root.TryGetProperty("requestId", out var id) || !_aiPending.TryGetValue(id.GetString() ?? "", out var channel)) return;
            var type = root.GetProperty("type").GetString();
            if (type == "ai.chunk")
            {
                var data = Convert.FromBase64String(root.GetProperty("contentBase64").GetString() ?? "");
                channel.Writer.TryWrite(new AiMessage(data, null));
            }
            else if (type == "ai.error")
                channel.Writer.TryComplete(new IOException(root.TryGetProperty("message", out var message) ? message.GetString() : "ai_proxy_error"));
            else
                channel.Writer.TryComplete();
        }

        public async Task StreamAiAsync(ReadOnlyMemory<byte> payload, Func<ReadOnlyMemory<byte>, Task> onChunk,
            CancellationToken cancellationToken)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var channel = Channel.CreateBounded<AiMessage>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
            _aiPending[requestId] = channel;
            try
            {
                await SendAsync(new { type = "ai.open", requestId, payloadBase64 = Convert.ToBase64String(payload.ToArray()) }, cancellationToken);
                await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken)) await onChunk(message.Payload);
            }
            finally
            {
                _aiPending.TryRemove(requestId, out _);
                try { await SendAsync(new { type = "ai.cancel", requestId }, CancellationToken.None); } catch { }
            }
        }

        public void Dispose()
        {
            foreach (var pending in _pending.Values) pending.TrySetCanceled();
            foreach (var pending in _taskPending.Values) pending.TrySetCanceled();
            foreach (var stream in _taskStreams.Values) stream.Writer.TryComplete(new IOException("media_agent_disconnected"));
            foreach (var pending in _aiPending.Values) pending.Writer.TryComplete(new IOException("media_agent_disconnected"));
            _sendLock.Dispose();
        }

        private sealed record AiMessage(byte[] Payload, string? Error);
    }
}
