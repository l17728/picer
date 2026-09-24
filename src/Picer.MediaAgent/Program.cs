using System.Net.WebSockets;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Windows.Forms;

namespace Picer.MediaAgent;

internal static class Program
{
    private const string SingleInstanceMutexName = "Global\\Picer.MediaAgent.Singleton";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, SingleInstanceMutexName, out var acquired);
        if (!acquired) return;
        ApplicationConfiguration.Initialize();
        using var agent = new AgentApplication();
        Application.Run();
    }
}

internal sealed class AgentApplication : IDisposable
{
    private const long MaxAssetBytes = 64L * 1024 * 1024;
    private readonly NotifyIcon _icon;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _serviceIp = Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_IP")?.Trim() ?? "";
    private readonly string _token = Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN")?.Trim() ?? "";
    private readonly byte[]? _spkiPin = ReadSpkiPin();
    private readonly string[] _roots;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _aiRequests = new(StringComparer.Ordinal);
    private readonly HttpClient _modelClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private Task? _loop;
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "picer-media-agent.log");

    public AgentApplication()
    {
        _roots = ReadRoots();
        var menu = new ContextMenuStrip();
        menu.Items.Add("重新连接", null, (_, _) => _stop.Cancel());
        menu.Items.Add("退出", null, (_, _) => Application.Exit());
        _icon = new NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Visible = true, Text = "Picer 原图代理", ContextMenuStrip = menu };
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    private static string[] ReadRoots()
    {
        var configured = Environment.GetEnvironmentVariable("PICER_MEDIA_AGENT_ROOTS");
        return (configured ?? Environment.GetEnvironmentVariable("PICER_DATA_ROOT") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static byte[]? ReadSpkiPin()
    {
        var value = Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_SPKI_PIN")?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : Convert.FromBase64String(value);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_serviceIp)) { _icon.Text = "Picer 原图代理（未配置服务 IP）"; Log("missing service ip"); return; }
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Log($"connecting ip={_serviceIp}, roots={string.Join(';', _roots)}");
                using var socket = new ClientWebSocket();
                socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    if (_spkiPin is null) return true;
                    var actual = new X509Certificate2(certificate).PublicKey.ExportSubjectPublicKeyInfo();
                    return CryptographicOperations.FixedTimeEquals(actual, _spkiPin);
                };
                if (!string.IsNullOrWhiteSpace(_token)) socket.Options.SetRequestHeader("Authorization", "Bearer " + _token);
                await socket.ConnectAsync(new Uri($"wss://{_serviceIp}:8445/api/agent/v1/connect"), cancellationToken);
                Log($"connected; socketState={socket.State}; processId={Environment.ProcessId}");
                _icon.Text = "Picer 原图代理（已连接）";
                await SendAsync(socket, new { type = "agent.register", agentId = Environment.MachineName, capabilities = new[] { "originalRead", "videoRead", "range" } }, cancellationToken);
                var receive = ReceiveAsync(socket, cancellationToken);
                var heartbeat = HeartbeatAsync(socket, cancellationToken);
                await Task.WhenAny(receive, heartbeat);
                socket.Abort();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                Log($"connection failed: {exception.GetType().Name}: {exception.Message}");
                _icon.Text = "Picer 原图代理（未连接）";
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}"); } catch { }
    }

    private async Task HeartbeatAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                await SendAsync(socket, new { type = "agent.heartbeat", utc = DateTime.UtcNow }, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Log($"heartbeat failed; socketState={socket.State}; exception={exception.GetType().Name}; inner={(exception.InnerException?.GetType().Name ?? "none")}");
            throw;
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveTextAsync(socket, buffer, cancellationToken);
            if (message is null) return;
            using var document = JsonDocument.Parse(message.Value);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type)) continue;
            switch (type.GetString())
            {
                case "asset.open":
                    _ = ServeAssetAsync(socket, root, cancellationToken);
                    break;
                case "ai.open":
                    _ = ServeAiAsync(socket, root, cancellationToken);
                    break;
                case "ai.cancel":
                    if (root.TryGetProperty("requestId", out var cancelId) && _aiRequests.TryGetValue(cancelId.GetString() ?? "", out var request)) request.Cancel();
                    break;
            }
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

    private async Task ServeAssetAsync(ClientWebSocket socket, JsonElement request, CancellationToken cancellationToken)
    {
        var requestId = request.TryGetProperty("requestId", out var id) ? id.GetString() : null;
        var requestedPath = request.TryGetProperty("path", out var path) ? path.GetString() : null;
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(requestedPath)) return;
        try
        {
            var fullPath = Path.GetFullPath(requestedPath);
            if (!_roots.Any(root => fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) throw new UnauthorizedAccessException("asset path is outside configured roots");
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > MaxAssetBytes) throw new FileNotFoundException("asset is missing or too large", fullPath);
            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            await SendAsync(socket, new { type = "asset.response", requestId, contentType = Mime(fullPath), contentBase64 = Convert.ToBase64String(bytes) }, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await SendAsync(socket, new { type = "asset.response", requestId, error = exception.Message }, cancellationToken);
        }
    }

    private async Task ServeAiAsync(ClientWebSocket socket, JsonElement request, CancellationToken cancellationToken)
    {
        var requestId = request.TryGetProperty("requestId", out var id) ? id.GetString() : null;
        if (string.IsNullOrWhiteSpace(requestId) || !request.TryGetProperty("payloadBase64", out var payloadElement)) return;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Log($"ai request accepted; requestId={requestId}; socketState={socket.State}");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_aiRequests.TryAdd(requestId, linked)) return;
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("PICER_LOCAL_MODEL_ENDPOINT")?.Trim()
                ?? "http://127.0.0.1:8090/api/vl/chat";
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("model endpoint must be loopback");
            using var upstream = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new ByteArrayContent(Convert.FromBase64String(payloadElement.GetString() ?? ""))
            };
            upstream.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await _modelClient.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            Log($"ai upstream response; requestId={requestId}; endpoint={uri.Host}{uri.AbsolutePath}; status={(int)response.StatusCode}; elapsedMs={started.ElapsedMilliseconds}");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"model_http_{(int)response.StatusCode}");
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            var buffer = new byte[32 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, linked.Token)) > 0)
                await SendAsync(socket, new { type = "ai.chunk", requestId, contentBase64 = Convert.ToBase64String(buffer, 0, read) }, linked.Token);
            await SendAsync(socket, new { type = "ai.done", requestId }, cancellationToken);
            Log($"ai request completed; requestId={requestId}; elapsedMs={started.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Log($"ai request failed; requestId={requestId}; elapsedMs={started.ElapsedMilliseconds}; exception={exception.GetType().Name}; inner={(exception.InnerException?.GetType().Name ?? "none")}; message={exception.Message}");
            try { await SendAsync(socket, new { type = "ai.error", requestId, message = exception.Message }, cancellationToken); } catch { }
        }
        finally { _aiRequests.TryRemove(requestId, out _); }
    }

    private async Task SendAsync(ClientWebSocket socket, object value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        await _sendLock.WaitAsync(cancellationToken);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        finally { _sendLock.Release(); }
    }

    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".avi" => "video/x-msvideo", _ => "application/octet-stream"
    };

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _icon.Visible = false;
        _icon.Dispose();
        _sendLock.Dispose();
        _modelClient.Dispose();
        foreach (var request in _aiRequests.Values) request.Cancel();
        _aiRequests.Clear();
        _stop.Dispose();
    }
}
