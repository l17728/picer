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
    private readonly object _sync = new();
    private readonly Dictionary<string, Flight> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ThumbnailWorkScheduler _downloads = new();
    private sealed class Flight
    {
        public readonly TaskCompletionSource<string?> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Stop = new();
        public int Subscribers;
        public bool Visible;
    }

    public RemoteThumbnailCache(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        foreach (var partial in Directory.EnumerateFiles(_root, "*.part", SearchOption.TopDirectoryOnly))
        {
            try { if (File.GetLastWriteTimeUtc(partial) < DateTime.UtcNow.AddDays(-1)) File.Delete(partial); } catch { }
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

    public async Task<string?> GetOrDownloadAsync(ImageRecord record, Func<CancellationToken, Task<Stream?>> openSource,
        CancellationToken cancellationToken = default, bool prefetch = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = GetKey(record);
        var existing = TryGet(record);
        if (existing is not null) return existing;
        Flight flight;
        var start = false;
        lock (_sync)
        {
            if (!_inFlight.TryGetValue(key, out flight!)) { _inFlight[key] = flight = new(); start = true; }
            flight.Subscribers++;
            if (!prefetch) Volatile.Write(ref flight.Visible, true);
        }
        if (start) _ = DownloadOwnedAsync(key, flight, openSource);
        else _downloads.RefreshPriorities();
        try { return await flight.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            var cancel = false;
            lock (_sync)
            {
                if (--flight.Subscribers == 0 && !flight.Ready.Task.IsCompleted)
                {
                    if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) _inFlight.Remove(key);
                    cancel = true;
                }
            }
            if (cancel)
            {
                try { flight.Stop.Cancel(); } catch (ObjectDisposedException) { }
                // The last subscriber leaves only after its orphaned writer has cleaned up.
                try { await flight.Ready.Task.ConfigureAwait(false); } catch (Exception) { }
            }
        }
    }

    public void Promote(ImageRecord record)
    {
        lock (_sync) if (_inFlight.TryGetValue(GetKey(record), out var flight)) Volatile.Write(ref flight.Visible, true);
        _downloads.RefreshPriorities();
    }

    private async Task DownloadOwnedAsync(string key, Flight owner,
        Func<CancellationToken, Task<Stream?>> openSource)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(owner.Stop.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var slot = await _downloads.EnterAsync(() => Volatile.Read(ref owner.Visible), deadline.Token).ConfigureAwait(false);
            owner.Ready.TrySetResult(await DownloadCoreAsync(key, owner, openSource, deadline.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { owner.Ready.TrySetCanceled(); }
        catch (Exception exception) { owner.Ready.TrySetException(exception); _ = owner.Ready.Task.Exception; }
        finally
        {
            lock (_sync) if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, owner)) _inFlight.Remove(key);
            owner.Stop.Dispose();
        }
    }

    private async Task<string?> DownloadCoreAsync(string key, Flight owner,
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
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_inFlight.TryGetValue(key, out var current) || !ReferenceEquals(current, owner)) throw new OperationCanceledException();
                if (!File.Exists(destination)) File.Move(temporary, destination);
            }
            return destination;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
