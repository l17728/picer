using Microsoft.Net.Http.Headers;
using Picer.Core.Gateway;
using Picer.Core.Logging;

namespace Picer.IndexService;

internal static class MediaAssetResults
{
    public static async Task<byte[]> PreviewAsync(AgentConnectionRegistry agents, string? agentId, string path, int size, CancellationToken token)
    {
        await using var stream = await agents.OpenMediaAsync(agentId, path, size, token);
        if (stream is not null)
        {
            if (stream.Length > 8 * 1024 * 1024) throw new IOException("media_preview_too_large");
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, token);
            return output.ToArray();
        }
        var legacy = await agents.RequestAsync(agentId, path, token) ?? throw new IOException("media_agent_offline");
        using var input = new MemoryStream(legacy.Content, writable: false);
        using var image = Picer.Core.Imaging.RasterImage.Load(input);
        image.ResizeToFit(size, size);
        using var result = new MemoryStream();
        image.SaveAsJpeg(result);
        return result.ToArray();
    }
    public static async Task<IResult> OpenAsync(AgentConnectionRegistry agents, HttpContext context, string? agentId, string path, int previewSize = 0)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        SocketMediaStream? stream;
        try
        {
            var expected = context.Request.Headers.IfMatch.ToString();
            stream = await agents.OpenMediaAsync(agentId, path, previewSize, context.RequestAborted, expected.Length > 0 ? expected : null, HttpMethods.IsHead(context.Request.Method));
        }
        catch (MediaSourceChangedException) { return Results.StatusCode(412); }
        if (stream is not null)
        {
            context.Response.Headers["X-Picer-Media-Streaming"] = "1";
            AppLogger.Info($"Media source ready; requestId={context.TraceIdentifier}; prepareMs={started.ElapsedMilliseconds}; bytes={stream.Length}; preview={previewSize}");
            try { return Results.Stream(stream, stream.Metadata.ContentType, entityTag: EntityTagHeaderValue.Parse(stream.Metadata.ETag), enableRangeProcessing: true); }
            catch { await stream.DisposeAsync(); throw; }
        }
        // Legacy agents cannot return metadata without uploading the complete original.
        // A capability probe must never trigger that transfer (especially for large videos).
        if (HttpMethods.IsHead(context.Request.Method)) return Results.StatusCode(405);
        var asset = await agents.RequestAsync(agentId, path, context.RequestAborted);
        return asset is null ? Results.StatusCode(503) : Results.File(asset.Value.Content, asset.Value.ContentType, enableRangeProcessing: true);
    }
}
