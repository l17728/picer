using System.Collections.Concurrent;
using System.Security.Cryptography;
using Picer.Core.Models;

namespace Picer.Core.Gateway;

/// <summary>
/// Persistent, content-addressed cache for thumbnails downloaded from an
/// IndexService.  A single in-flight download is shared by all cards that
/// refer to the same source image, so virtualization/scrolling cannot issue
/// duplicate requests.
/// </summary>
public sealed class RemoteThumbnailCache
{
    private const string CacheVersion = "v1";
    private readonly string _root;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _downloadSlots = new(8, 8);

    public RemoteThumbnailCache(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        foreach (var partial in Directory.EnumerateFiles(_root, "*.part", SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(partial); } catch { }
        }
    }

    public string GetKey(ImageRecord record)
    {
        var identity = !string.IsNullOrWhiteSpace(record.ContentSha256)
            ? record.ContentSha256
            : !string.IsNullOrWhiteSpace(record.Sha256) ? record.Sha256 : $"id-{record.Id}";
        var normalized = identity!.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(value => !Uri.IsHexDigit(value)))
            normalized = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return $"{CacheVersion}-{normalized}";
    }

    public string? TryGet(ImageRecord record)
    {
        var path = Path.Combine(_root, GetKey(record) + ".jpg");
        if (!File.Exists(path)) return null;
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
        return path;
    }

    public Task<string?> GetOrDownloadAsync(ImageRecord record, Func<Task<Stream?>> openSource,
        CancellationToken cancellationToken = default) => GetOrDownloadAsync(record, _ => openSource(), cancellationToken);

    public Task<string?> GetOrDownloadAsync(ImageRecord record, Func<CancellationToken, Task<Stream?>> openSource,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = GetKey(record);
        var existing = TryGet(record);
        if (existing is not null) return Task.FromResult<string?>(existing);
        Lazy<Task<string?>> candidate = null!;
        candidate = new Lazy<Task<string?>>(() => DownloadOwnedAsync(key, candidate, openSource), LazyThreadSafetyMode.ExecutionAndPublication);
        var task = _inFlight.GetOrAdd(key, candidate).Value;
        // Caller cancellation stops only that subscriber. The bounded shared download
        // remains useful to other visible cards and the persistent cache.
        _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task.WaitAsync(cancellationToken);
    }

    private async Task<string?> DownloadOwnedAsync(string key, Lazy<Task<string?>> owner,
        Func<CancellationToken, Task<Stream?>> openSource)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await _downloadSlots.WaitAsync(deadline.Token).ConfigureAwait(false);
            try { return await DownloadCoreAsync(key, openSource, deadline.Token).ConfigureAwait(false); }
            finally { _downloadSlots.Release(); }
        }
        finally { ((ICollection<KeyValuePair<string, Lazy<Task<string?>>>>)_inFlight).Remove(new(key, owner)); }
    }

    private async Task<string?> DownloadCoreAsync(string key,
        Func<CancellationToken, Task<Stream?>> openSource, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(_root, key + ".jpg");
        if (File.Exists(destination)) return destination;
        await using var source = await openSource(cancellationToken).ConfigureAwait(false);
        if (source is null) return null;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(destination)) File.Move(temporary, destination);
            return destination;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
