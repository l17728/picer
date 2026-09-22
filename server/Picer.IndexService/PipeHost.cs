using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Picer.Core.Gateway;
using Picer.Core.Indexing;
using Picer.Core.Logging;
using Picer.Core.Search;

namespace Picer.IndexService;

internal sealed record PipeListRequest(long? SourceId = null, bool IncludeMissing = false, int PageSize = 200, string? Cursor = null);
internal sealed record PipeImageRequest(long Id = 0);
internal sealed record PipeVideoListRequest(long? SourceId = null, int PageSize = 200, string? Cursor = null);
internal sealed record PipeSearchRequest(SearchFilters? Filters = null, int PageSize = 200, string? Cursor = null);
internal sealed record PipeSourceRequest(string Path = "");
internal sealed record PipeIdRequest(long Id = 0);
internal sealed record PipeJobRequest(string JobId = "");
internal sealed record PipeScanRequest(long SourceId = 0);
internal sealed record PipeVerifiedChangesRequest(IReadOnlyList<long> ImageIds);
internal sealed record PipeBackupRequest(string DestinationPath = "");

public sealed class NamedPipeIndexServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName;
    private readonly string _token;
    private readonly IndexApiHandlers _handlers;
    private readonly IndexEngine _engine;
    private readonly ScanJobManager _jobs;
    private readonly CancellationTokenSource _cancellation = new();

    public NamedPipeIndexServer(string pipeName, string token, IndexApiHandlers handlers, IndexEngine engine, ScanJobManager jobs)
    {
        _pipeName = pipeName;
        _token = token;
        _handlers = handlers;
        _engine = engine;
        _jobs = jobs;
    }

    public async Task RunAsync()
    {
        var cancellationToken = _cancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
                AppLogger.Info($"Pipe 客户端已连接: {_pipeName}");
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                break;
            }
            try
            {
                await ServeClientAsync(server, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException or OperationCanceledException or InvalidDataException)
            {
                AppLogger.Info($"Pipe 客户端断开: {exception.GetType().Name}");
            }
            finally
            {
                await server.DisposeAsync();
            }
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        using var auth = JsonDocument.Parse(await PipeFrames.ReadAsync(stream, cancellationToken));
        var provided = auth.RootElement.GetProperty("token").GetString() ?? "";
        var authorized = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(_token));
        await PipeFrames.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new { ok = authorized }), cancellationToken);
        if (!authorized) return;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = JsonDocument.Parse(await PipeFrames.ReadAsync(stream, cancellationToken));
            var op = request.RootElement.GetProperty("op").GetString() ?? "";
            var payload = request.RootElement.TryGetProperty("payload", out var payloadElement) && payloadElement.ValueKind == JsonValueKind.Object
                ? payloadElement
                : (JsonElement?)null;
            var listRequest = payload is null ? new PipeListRequest() : payload.Value.Deserialize<PipeListRequest>(Json) ?? new PipeListRequest();
            var imageRequest = payload is null ? new PipeImageRequest() : payload.Value.Deserialize<PipeImageRequest>(Json) ?? new PipeImageRequest();
            var videoListRequest = payload is null ? new PipeVideoListRequest() : payload.Value.Deserialize<PipeVideoListRequest>(Json) ?? new PipeVideoListRequest();
            var searchRequest = payload is null ? new PipeSearchRequest() : payload.Value.Deserialize<PipeSearchRequest>(Json) ?? new PipeSearchRequest();
            var scanRequest = payload is null ? new PipeScanRequest() : payload.Value.Deserialize<PipeScanRequest>(Json) ?? new PipeScanRequest();
            var sourceRequest = payload is null ? new PipeSourceRequest() : payload.Value.Deserialize<PipeSourceRequest>(Json) ?? new PipeSourceRequest();
            var idRequest = payload is null ? new PipeIdRequest() : payload.Value.Deserialize<PipeIdRequest>(Json) ?? new PipeIdRequest();
            var jobRequest = payload is null ? new PipeJobRequest() : payload.Value.Deserialize<PipeJobRequest>(Json) ?? new PipeJobRequest();
            var changesRequest = payload is null || payload.Value.ValueKind != JsonValueKind.Object
                ? null
                : payload.Value.Deserialize<PipeVerifiedChangesRequest>(Json);
            var changes = changesRequest?.ImageIds ?? [];
            var backupRequest = payload is null || payload.Value.ValueKind != JsonValueKind.Object
                ? new PipeBackupRequest()
                : payload.Value.Deserialize<PipeBackupRequest>(Json) ?? new PipeBackupRequest();
            var body = op switch
            {
                "health" => JsonSerializer.SerializeToElement(_handlers.Health()),
                "list" => JsonSerializer.SerializeToElement(await _handlers.ListAsync(listRequest.SourceId, listRequest.IncludeMissing, listRequest.PageSize, listRequest.Cursor, cancellationToken)),
                "image" => JsonSerializer.SerializeToElement(await _handlers.GetAsync(imageRequest.Id, cancellationToken)),
                "videos" => JsonSerializer.SerializeToElement(await _handlers.ListVideosAsync(videoListRequest.SourceId, videoListRequest.PageSize, videoListRequest.Cursor, cancellationToken)),
                "video" => JsonSerializer.SerializeToElement(await _handlers.GetVideoAsync(imageRequest.Id, cancellationToken)),
                "thumb" => await ThumbnailElementAsync(imageRequest.Id, cancellationToken),
                "search" => JsonSerializer.SerializeToElement(await _handlers.SearchAsync(new GatewaySearchRequest(searchRequest.Filters, searchRequest.PageSize, searchRequest.Cursor), cancellationToken)),
                "sources" => JsonSerializer.SerializeToElement(_handlers.ListSources()),
                "tags" => JsonSerializer.SerializeToElement(_handlers.ListTags()),
                "albums" => JsonSerializer.SerializeToElement(_handlers.ListAlbums()),
                "metadata" => JsonSerializer.SerializeToElement(_handlers.GetMetadata()),
                "people" => JsonSerializer.SerializeToElement(_handlers.ListPeople()),
                "add_source" => JsonSerializer.SerializeToElement(_handlers.RegisterSource(sourceRequest.Path)),
                "remove_source" => JsonSerializer.SerializeToElement(new PipeFlagResponse(_handlers.RemoveSource(idRequest.Id))),
                "verified_changes" => JsonSerializer.SerializeToElement(new GatewayVerifiedChangesResult(_handlers.RemoveRecycledRecords(changes))),
                "backup" => JsonSerializer.SerializeToElement(new GatewayBackupResult(_handlers.Backup(backupRequest.DestinationPath))),
                "scan" => JsonSerializer.SerializeToElement(_handlers.GetSource(scanRequest.SourceId) is { } source
                    ? new GatewayScanStarted(_jobs.Start(_engine, source.Id, source.Path))
                    : throw new KeyNotFoundException($"来源不存在：{scanRequest.SourceId}")),
                "job" => JsonSerializer.SerializeToElement(_jobs.Get(jobRequest.JobId)),
                "cancel_job" => JsonSerializer.SerializeToElement(new PipeFlagResponse(_jobs.Cancel(jobRequest.JobId))),
                _ => throw new InvalidDataException($"Unknown pipe op '{op}'.")
            };
            await PipeFrames.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new { ok = true, body }), cancellationToken);
        }
    }

    private async Task<JsonElement> ThumbnailElementAsync(long id, CancellationToken cancellationToken)
    {
        await using var stream = await _handlers.OpenThumbnailAsync(id, cancellationToken);
        if (stream is null) return JsonSerializer.SerializeToElement(new { found = false, dataBase64 = (string?)null });
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes, cancellationToken);
        return JsonSerializer.SerializeToElement(new { found = true, dataBase64 = Convert.ToBase64String(bytes.ToArray()) });
    }

    public void Dispose() => _cancellation.Cancel();
}
