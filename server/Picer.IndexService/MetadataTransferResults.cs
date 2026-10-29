using Picer.Core.Gateway;

namespace Picer.IndexService;

internal static class MetadataTransferResults
{
    public static IResult Create(HttpContext context, GatewayMetadata metadata)
    {
        if (!context.Request.Headers.TryGetValue(MetadataTransfer.RequestHeader, out var hashes)) return Results.Json(metadata);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var transfer = MetadataTransfer.Create(metadata, hashes.ToString());
        context.Response.Headers[MetadataTransfer.ResponseHeader] = "1";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Append("Server-Timing", $"metadata_diff;dur={timer.ElapsedMilliseconds}");
        return Results.Json(transfer);
    }
}
