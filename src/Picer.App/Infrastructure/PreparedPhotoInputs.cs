using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using Picer.Core.Gateway;
using Picer.Core.Models;

namespace Picer.App.Infrastructure;

/// <summary>At most three verified derivatives, each at most 8 MiB. Originals are never prefetched.</summary>
internal sealed class PreparedPhotoInputs(IMediaAssetGateway assets, string root) : IDisposable
{
    private sealed record Entry(string? Path, Exception? Error);
    private readonly SemaphoreSlim _slots = new(3, 3);
    private readonly ConcurrentDictionary<ImageRecord, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    public async Task<IDisposable?> PrepareAsync(ImageRecord image, CancellationToken token)
    {
        using var trace = new Picer.Core.Logging.MediaReadTrace("photo-prefetch", image.Id);
        trace.Mark("queued");
        await _slots.WaitAsync(token).ConfigureAwait(false);
        trace.Mark("admitted");
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".jpg");
        Entry entry;
        try
        {
            Directory.CreateDirectory(root);
            await using var stream = await assets.OpenPhotoAnalysisInputAsync(image, token).ConfigureAwait(false);
            if (stream is null) entry = new(null, null);
            else
            {
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                var buffer = new byte[65536]; long total = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > 8 * 1024 * 1024) throw new InvalidDataException("prepared_photo_too_large");
                    await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                if (total == 0) throw new InvalidDataException("prepared_photo_empty");
                trace.Mark("prepared", total);
                entry = new(path, null);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Cleanup(path); _slots.Release(); throw; }
        catch (Exception exception) { trace.Failure(exception); Cleanup(path); entry = new(null, exception); }
        if (!_entries.TryAdd(image, entry)) { Cleanup(path); _slots.Release(); return null; }
        return new Lease(() => { if (_entries.TryRemove(image, out var removed)) Cleanup(removed.Path); _slots.Release(); });
    }
    public Stream? Open(ImageRecord image, out bool prepared)
    {
        prepared = _entries.TryGetValue(image, out var entry);
        if (entry?.Error is { } error) ExceptionDispatchInfo.Capture(error).Throw();
        return entry?.Path is { } path ? File.OpenRead(path) : null;
    }
    private static void Cleanup(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Picer.Core.Logging.AnalysisDiagnosticLog.Warn($"Prepared photo cleanup deferred; type={exception.GetType().Name}"); }
    }
    public void Dispose()
    {
        foreach (var entry in _entries.Values) Cleanup(entry.Path);
        _entries.Clear(); _slots.Dispose();
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
