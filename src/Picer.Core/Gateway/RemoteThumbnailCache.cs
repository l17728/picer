using System.Security.Cryptography;
using Picer.Core.Models;
using Picer.Core.Security;

namespace Picer.Core.Gateway;

/// <summary>
/// Persistent, content-addressed cache for thumbnails downloaded from an
/// IndexService.  A single in-flight download is shared by all cards that
/// refer to the same source image, so virtualization/scrolling cannot issue
/// duplicate requests.
/// </summary>
public sealed class RemoteThumbnailCache : IDisposable
{
    private const string CacheVersion = "v1";
    private readonly string _root;
    private readonly MediaFileCipher? _cipher;
    private readonly object _sync = new();
    private readonly Dictionary<string, Flight> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ThumbnailWorkScheduler _downloads = new(3, 1);
    public static ImageRecord DerivedRecord(string kind, string versionedIdentity) => new()
    {
        ContentSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(kind + ":" + versionedIdentity))).ToLowerInvariant()
    };
    private sealed class Flight
    {
        public readonly TaskCompletionSource<string?> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Stop = new();
        public int Subscribers;
        public bool Visible;
    }

    public RemoteThumbnailCache(string root, MediaFileCipher? cipher = null)
    {
        _root = Path.GetFullPath(root);
        _cipher = cipher;
        Directory.CreateDirectory(_root);
        foreach (var partial in Directory.EnumerateFiles(_root, "*.part", SearchOption.TopDirectoryOnly))
        {
            try { if (File.GetLastWriteTimeUtc(partial) < DateTime.UtcNow.AddDays(-1)) File.Delete(partial); } catch { }
        }
    }

    public bool IsCachePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootPrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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
        if (!File.Exists(path))
        {
            var legacy = LegacyClassificationPath(record);
            if (legacy is null || !File.Exists(legacy)) return null;
            path = legacy;
        }
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
        return path;
    }

    // Older classification windows hashed the tagged identity, including the "sha256:" prefix.
    // Read under its original filename because encrypted entries authenticate the filename.
    private string? LegacyClassificationPath(ImageRecord record)
    {
        var digest = string.IsNullOrWhiteSpace(record.ContentSha256) ? record.Sha256 : record.ContentSha256;
        if (digest is not { Length: 64 } || !digest.All(Uri.IsHexDigit)) return null;
        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes("sha256:" + digest.ToLowerInvariant()));
        return Path.Combine(_root, $"{CacheVersion}-{Convert.ToHexString(hash).ToLowerInvariant()}.jpg");
    }

    public Stream OpenRead(ImageRecord record) =>
        OpenReadPath(TryGet(record) ?? Path.Combine(_root, GetKey(record) + ".jpg"));

    public Stream OpenReadPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsCachePath(fullPath))
            throw new UnauthorizedAccessException("thumbnail_cache_path_outside_root");
        if (_cipher is null) return File.OpenRead(fullPath);
        var bytes = File.ReadAllBytes(fullPath);
        var plaintext = _cipher.UnprotectOrReadLegacy(bytes, Path.GetFileName(fullPath), out var encrypted);
        CryptographicOperations.ZeroMemory(bytes);
        try
        {
            if (!encrypted) SaveProtectedAtomic(fullPath, plaintext);
            return new ZeroingMemoryStream(plaintext);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    public (int Migrated, int Failed) ProtectExistingCacheFiles(CancellationToken cancellationToken = default)
    {
        if (_cipher is null) return (0, 0);
        var migrated = 0;
        var failed = 0;
        foreach (var path in Directory.EnumerateFiles(_root, "*.jpg", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var alreadyProtected = IsProtected(path);
                using var content = OpenReadPath(path);
                if (!alreadyProtected) migrated++;
            }
            catch (OperationCanceledException) { throw; }
            catch { failed++; }
        }
        return (migrated, failed);
    }

    private bool IsProtected(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return _cipher!.IsEncrypted(bytes);
    }

    public void Invalidate(ImageRecord record)
    {
        lock (_sync)
        {
            DeleteEntry(Path.Combine(_root, GetKey(record) + ".jpg"));
            if (LegacyClassificationPath(record) is { } legacy) DeleteEntry(legacy);
        }

        static void DeleteEntry(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
        using var trace = new Picer.Core.Logging.MediaReadTrace("thumbnail-download");
        trace.Mark("queued");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(owner.Stop.Token);
        try
        {
            using var slot = await _downloads.EnterAsync(() => Volatile.Read(ref owner.Visible), deadline.Token).ConfigureAwait(false);
            // Waiting behind the viewport is not a network timeout.
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            trace.Mark("admitted");
            owner.Ready.TrySetResult(await DownloadCoreAsync(key, owner, openSource, deadline.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { owner.Ready.TrySetCanceled(); }
        catch (Exception exception) { trace.Failure(exception); owner.Ready.TrySetException(exception); _ = owner.Ready.Task.Exception; }
        finally
        {
            lock (_sync) if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, owner)) _inFlight.Remove(key);
            owner.Stop.Dispose();
        }
    }

    private async Task<string?> DownloadCoreAsync(string key, Flight owner,
        Func<CancellationToken, Task<Stream?>> openSource, CancellationToken cancellationToken)
    {
        using var trace = new Picer.Core.Logging.MediaReadTrace("thumbnail-cache-write");
        var destination = Path.Combine(_root, key + ".jpg");
        if (File.Exists(destination)) return destination;
        await using var source = await openSource(cancellationToken).ConfigureAwait(false);
        trace.Mark("source_ready");
        if (source is null) return null;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            byte[]? plaintext = null;
            byte[]? stored = null;
            try
            {
                if (_cipher is null)
                {
                    await using var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await CopyBoundedAsync(source, target, cancellationToken).ConfigureAwait(false);
                    if (target.Length == 0) throw new IOException("Empty thumbnail response.");
                    trace.Mark("body_copied", target.Length);
                }
                else
                {
                    using var buffer = new MemoryStream();
                    try
                    {
                        await CopyBoundedAsync(source, buffer, cancellationToken, 32 * 1024 * 1024).ConfigureAwait(false);
                        if (buffer.Length == 0) throw new IOException("Empty thumbnail response.");
                        plaintext = buffer.ToArray();
                        stored = _cipher.Protect(plaintext, Path.GetFileName(destination));
                        await File.WriteAllBytesAsync(temporary, stored, cancellationToken).ConfigureAwait(false);
                        trace.Mark("body_encrypted", stored.Length);
                    }
                    finally
                    {
                        if (buffer.TryGetBuffer(out var segment) && segment.Array is not null)
                            CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
                    }
                }
            }
            finally
            {
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
                if (stored is not null) CryptographicOperations.ZeroMemory(stored);
            }
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_inFlight.TryGetValue(key, out var current) || !ReferenceEquals(current, owner)) throw new OperationCanceledException();
                if (!File.Exists(destination)) File.Move(temporary, destination);
            }
            trace.Mark("published");
            return destination;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private void SaveProtectedAtomic(string destination, ReadOnlySpan<byte> plaintext)
    {
        var encrypted = _cipher!.Protect(plaintext, Path.GetFileName(destination));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, destination, overwrite: true); }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, CancellationToken token,
        long maximumBytes = 32 * 1024 * 1024)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maximumBytes) throw new IOException("thumbnail_response_too_large");
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private sealed class ZeroingMemoryStream(byte[] buffer) : MemoryStream(buffer, 0, buffer.Length, writable: false, publiclyVisible: true)
    {
        private byte[]? _ownedBuffer = buffer;

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _ownedBuffer, null) is { } owned)
                CryptographicOperations.ZeroMemory(owned);
            base.Dispose(disposing);
        }
    }

    public void Dispose() => _cipher?.Dispose();
}
