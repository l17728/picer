using System.IO.Pipes;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.Core.Gateway;

public sealed record PipeThumbnailResponse(bool Found, string? DataBase64);

public sealed record PipeFlagResponse(bool Value);

public sealed class PipeIndexGateway : IIndexGateway, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName;
    private readonly string _token;
    private NamedPipeClientStream? _stream;

    public PipeIndexGateway(string pipeName, string token)
    {
        _pipeName = pipeName;
        _token = token;
    }

    public async Task<GatewayHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        await CallAsync<GatewayHealth>("health", null, cancellationToken) ?? throw new InvalidOperationException("Empty health payload.");

    public async Task<GatewayPage> ListImagesAsync(long? sourceId = null, bool includeMissing = false, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default) =>
        await CallAsync<GatewayPage>("list", new { sourceId, includeMissing, pageSize = Math.Clamp(pageSize, 1, 500), cursor }, cancellationToken)
            ?? throw new InvalidOperationException("Empty list payload.");

    public async Task<ImageRecord?> GetImageAsync(long id, CancellationToken cancellationToken = default) =>
        await CallAsync<ImageRecord>("image", new { id }, cancellationToken);

    public async Task<Stream?> OpenThumbnailAsync(long imageId, CancellationToken cancellationToken = default)
    {
        var body = await CallAsync<PipeThumbnailResponse>("thumb", new { id = imageId }, cancellationToken);
        if (body is null || !body.Found || body.DataBase64 is null) return null;
        return new MemoryStream(Convert.FromBase64String(body.DataBase64), writable: false);
    }

    public async Task<GatewayPage> SearchAsync(SearchFilters filters, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default) =>
        await CallAsync<GatewayPage>("search", new { filters, pageSize = Math.Clamp(pageSize, 1, 500), cursor }, cancellationToken)
            ?? throw new InvalidOperationException("Empty search payload.");

    private async Task<NamedPipeClientStream> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_stream is { IsConnected: true }) return _stream;
        _stream?.Dispose();
        var stream = await PipeFrames.ConnectNamedPipeAsync(_pipeName, cancellationToken);
        await PipeFrames.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new { token = _token }), cancellationToken);
        using var authDocument = JsonDocument.Parse(await PipeFrames.ReadAsync(stream, cancellationToken));
        if (authDocument.RootElement.GetProperty("ok").GetBoolean() != true)
            throw new UnauthorizedAccessException("Pipe gateway rejected the bearer token.");
        _stream = stream;
        return stream;
    }

    private async Task<T?> CallAsync<T>(string op, object? payload, CancellationToken cancellationToken) where T : class
    {
        var request = JsonSerializer.SerializeToUtf8Bytes(new { op, payload }, Json);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var stream = await EnsureConnectedAsync(cancellationToken);
                await PipeFrames.WriteAsync(stream, request, cancellationToken);
                using var document = JsonDocument.Parse(await PipeFrames.ReadAsync(stream, cancellationToken));
                var root = document.RootElement;
                if (!root.GetProperty("ok").GetBoolean())
                    throw new HttpRequestException($"Pipe gateway error: {root.GetProperty("error").GetString()}");
                if (!root.TryGetProperty("body", out var bodyElement) || bodyElement.ValueKind == JsonValueKind.Null)
                    return null;
                return bodyElement.Deserialize<T>(Json);
            }
            catch (Exception exception) when (attempt == 0 && exception is IOException or EndOfStreamException or ObjectDisposedException)
            {
                _stream?.Dispose();
                _stream = null;
            }
        }
    }

    public async Task<IReadOnlyList<SourceFolder>> ListSourcesAsync(CancellationToken cancellationToken = default) =>
        await CallAsync<IReadOnlyList<SourceFolder>>("sources", null, cancellationToken) ?? [];

    public async Task<SourceFolder> RegisterSourceAsync(string path, CancellationToken cancellationToken = default) =>
        await CallAsync<SourceFolder>("add_source", new { path }, cancellationToken)
            ?? throw new InvalidOperationException("Empty source payload.");

    public async Task<bool> RemoveSourceAsync(long sourceId, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<PipeFlagResponse>("remove_source", new { id = sourceId }, cancellationToken);
        return response?.Value ?? false;
    }

    public async Task<string> StartScanAsync(long sourceId, CancellationToken cancellationToken = default)
    {
        var started = await CallAsync<GatewayScanStarted>("scan", new { sourceId }, cancellationToken);
        return started?.JobId ?? throw new InvalidOperationException("Empty scan payload.");
    }
    public Task<ScanJobInfo?> GetScanJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        CallAsync<ScanJobInfo>("job", new { jobId }, cancellationToken);

    public async Task<bool> CancelScanAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<PipeFlagResponse>("cancel_job", new { jobId }, cancellationToken);
        return response?.Value ?? false;
    }

    public async Task<int> RemoveRecycledRecordsAsync(IReadOnlyList<long> imageIds, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync<GatewayVerifiedChangesResult>("verified_changes", new { imageIds }, cancellationToken);
        return result?.Removed ?? 0;
    }

    public async Task<string> BackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync<GatewayBackupResult>("backup", new { destinationPath }, cancellationToken);
        return result?.Path ?? throw new InvalidOperationException("Empty backup payload.");
    }

    public void Dispose() => _stream?.Dispose();
}
