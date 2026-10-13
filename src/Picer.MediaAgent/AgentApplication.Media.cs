using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Picer.Core.Gateway;
using Picer.Core.Imaging;

namespace Picer.MediaAgent;

internal sealed partial class AgentApplication
{
    private readonly Picer.Core.Indexing.BackgroundWorkCoordinator _maintenanceActivity = new();
    private readonly SemaphoreSlim _mediaReaders = new(4, 4);
    private readonly SemaphoreSlim _previewReaders = new(2, 2);
    private readonly SemaphoreSlim _analysisReaders = new(2, 2);
    private readonly MediaPreviewCache _mediaPreviews = new();
    private readonly MediaReadLeasePool _mediaLeases = new();

    private async Task ServeMediaAsync(JsonElement request, CancellationToken token)
    {
        using var foreground = _maintenanceActivity.EnterForeground();
        var id = request.GetProperty("requestId").GetString()!;
        var size = request.GetProperty("previewSize").GetInt32();
        var gate = size == 1280 ? _analysisReaders : size > 0 ? _previewReaders : _mediaReaders;
        var elapsed = Stopwatch.StartNew();
        var entered = false;
        ClientWebSocket? socket = null;
        try
        {
            var settings = _settings ?? throw new IOException("media_settings_missing");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            await gate.WaitAsync(deadline.Token); entered = true;
            var queueMs = elapsed.ElapsedMilliseconds;
            if (_accountClient is { } account) socket = await account.ConnectWebSocketAsync("api/agent/v1/media/" + id, deadline.Token);
            else
            {
                socket = new ClientWebSocket();
                socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) => MediaAgentSettings.MatchesCertificate(certificate, Convert.FromBase64String(settings.SpkiPin));
                socket.Options.SetRequestHeader("Authorization", "Bearer " + settings.Token);
                var endpoint = new UriBuilder(settings.ServiceUrl) { Scheme = "wss", Path = "/api/agent/v1/media/" + id };
                await socket.ConnectAsync(endpoint.Uri, deadline.Token);
            }
            await MediaFileSession.ServeAsync(socket, request.GetProperty("path").GetString()!, _roots, size,
                Mime(request.GetProperty("path").GetString()!), _mediaPreviews,
                metrics => Log($"media block; requestId={id}; queueMs={queueMs}; {metrics}"), token, _mediaLeases,
                request.TryGetProperty("expectedETag", out var expected) && expected.ValueKind == JsonValueKind.String ? expected.GetString() : null,
                request.TryGetProperty("keepLease", out var keep) && keep.GetBoolean(),
                request.TryGetProperty("sourceFingerprint", out var fingerprint) && fingerprint.ValueKind == JsonValueKind.Object
                    ? fingerprint.Deserialize<MediaWire.SourceFingerprint>() : null);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or WebSocketException or ArgumentException)
        {
            Log($"media closed; requestId={id}; elapsedMs={elapsed.ElapsedMilliseconds}; reason={exception.GetType().Name}");
            if (socket?.State == WebSocketState.Open)
            {
                try { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await MediaWire.SendJsonAsync(socket, new MediaWire.Metadata(0, "", "", exception is MediaSourceChangedException ? "media_source_changed" : "media_source_unavailable"), stop.Token); }
                catch (Exception) { }
            }
        }
        finally { socket?.Abort(); socket?.Dispose(); if (entered) gate.Release(); }
    }
}
