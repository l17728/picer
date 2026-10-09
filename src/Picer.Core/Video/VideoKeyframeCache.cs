using Picer.Core.Imaging;
namespace Picer.Core.Video;

/// <summary>Publishes complete downloads only; failed requests never replace a usable cache entry.</summary>
public static class VideoKeyframeCache
{
    // Bounded stripes avoid leaking one semaphore per frame in a large library.
    private static readonly object[] PublishLocks = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    public static async Task WriteAsync(Stream source, string target, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        var temporary = target + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            // Windows requires the exclusive output handle to close BEFORE the atomic rename.
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                if (output.Length == 0) throw new IOException("IndexService returned an empty keyframe asset.");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            // A non-empty HTTP 200 payload can still be truncated or not an
            // image. Never publish it as a permanent successful thumbnail.
            using (await RasterImage.LoadAsync(temporary, cancellationToken).ConfigureAwait(false)) { }
            cancellationToken.ThrowIfCancellationRequested();
            var stripe = (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetFullPath(target)) % (uint)PublishLocks.Length;
            lock (PublishLocks[stripe])
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The cache key identifies immutable content. A competing
                // reader may already have opened the first successful copy.
                // Do not replace it (or invalidate the reader's file handle).
                if (!File.Exists(target) || new FileInfo(target).Length == 0)
                    File.Move(temporary, target, overwrite: true);
            }
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }
}
