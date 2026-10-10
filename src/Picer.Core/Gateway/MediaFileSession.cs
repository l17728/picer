using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Picer.Core.Imaging;

namespace Picer.Core.Gateway;

public static class MediaFileSession
{
    public static async Task ServeAsync(WebSocket socket, string requestedPath, IReadOnlyList<string> roots, int previewSize,
        string contentType, MediaPreviewCache cache, Action<string> log, CancellationToken token,
        MediaReadLeasePool? leases = null, string? expectedETag = null, bool keepLease = false)
    {
        var timer = Stopwatch.StartNew();
        var path = MediaSourceIdentity.ResolveAllowed(requestedPath, roots);
        using var operation = Picer.Core.Video.VideoOperationLifetime.Begin(path, token);
        token = operation.Token;
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.RandomAccess);
        using var lease = previewSize == 0 && leases is not null ? leases.Acquire(path, expectedETag) : null;
        var etag = lease?.ETag ?? MediaSourceIdentity.GetETag(source);
        MediaSourceIdentity.ResolveAllowed(path, roots);
        MemoryStream? preview = null;
        long decodeMs = 0, encodeMs = 0;
        try
        {
            Stream input = source;
            if (previewSize != 0)
            {
                if (previewSize is not (256 or 1600)) throw new InvalidDataException("media_preview_size_invalid");
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(etag + ":" + previewSize))).ToLowerInvariant();
                var bytes = await cache.GetAsync(key, ct => Task.Run(() =>
                {
                    var stage = Stopwatch.StartNew();
                    using var image = RasterImage.Load(source);
                    decodeMs = stage.ElapsedMilliseconds;
                    stage.Restart();
                    if (image.Width > previewSize || image.Height > previewSize) image.ResizeToFit(previewSize, previewSize);
                    using var output = new MemoryStream();
                    image.SaveAsJpeg(output);
                    encodeMs = stage.ElapsedMilliseconds;
                    ct.ThrowIfCancellationRequested();
                    return output.ToArray();
                }, ct), token);
                preview = new MemoryStream(bytes, writable: false);
                input = preview;
                etag = "\"" + key + "\"";
                contentType = "image/jpeg";
            }
            var prepareMs = timer.ElapsedMilliseconds;
            await MediaWire.SendJsonAsync(socket, new MediaWire.Metadata(input.Length, contentType, etag), token);
            long total = 0, readMs = 0, sendMs = 0;
            var block = new byte[MediaWire.BlockSize];
            while (socket.State == WebSocketState.Open)
            {
                var request = await MediaWire.ReceiveJsonAsync<MediaWire.ReadRequest>(socket, token);
                if (request.Offset < 0 || request.Count is < 1 or > MediaWire.BlockSize || request.Offset > input.Length - request.Count)
                    throw new InvalidDataException("media_range_invalid");
                var start = timer.ElapsedMilliseconds;
                input.Position = request.Offset;
                await input.ReadExactlyAsync(block.AsMemory(0, request.Count), token);
                readMs += timer.ElapsedMilliseconds - start;
                start = timer.ElapsedMilliseconds;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMinutes(2));
                await socket.SendAsync(block.AsMemory(0, request.Count), WebSocketMessageType.Binary, true, deadline.Token);
                sendMs += timer.ElapsedMilliseconds - start;
                total += request.Count;
                log($"bytes={total}; prepareMs={prepareMs}; decodeMs={decodeMs}; encodeMs={encodeMs}; readMs={readMs}; sendMs={sendMs}");
            }
        }
        finally
        {
            if (preview is not null) await preview.DisposeAsync();
            if (lease is not null && !keepLease && expectedETag is null) leases!.Release(lease.ETag);
        }
    }
}
