using System.Net.WebSockets;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace Picer.MediaAgent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
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
                Log("connected");
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
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            await SendAsync(socket, new { type = "agent.heartbeat", utc = DateTime.UtcNow }, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
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
            if (root.TryGetProperty("type", out var type) && type.GetString() == "asset.open") await ServeAssetAsync(socket, root, cancellationToken);
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
        _stop.Dispose();
    }
}
