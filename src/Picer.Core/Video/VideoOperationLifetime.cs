using Picer.Core.Indexing;

namespace Picer.Core.Video;

/// <summary>Coordinates cache writers with explicit index removal in this process.</summary>
public static class VideoOperationLifetime
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, HashSet<Operation>> Active = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Purging = new(StringComparer.OrdinalIgnoreCase);
    private static string Key(string path) => MediaSourcePath.ForStorage(path).Replace('\\', '/').TrimEnd('/');

    public sealed class Operation : IDisposable
    {
        internal readonly string Path;
        internal readonly CancellationTokenSource Cancellation;
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Operation(string path, CancellationToken token) { Path = path; Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); }
        public CancellationToken Token => Cancellation.Token;
        public void Dispose()
        {
            lock (Gate)
            {
                if (Active.TryGetValue(Path, out var operations) && operations.Remove(this))
                {
                    if (operations.Count == 0) Active.Remove(Path);
                    Finished.TrySetResult(); Cancellation.Dispose();
                }
            }
        }
    }

    public static Operation Begin(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (Gate)
        {
            var key = Key(path);
            if (Purging.Contains(key)) throw new OperationCanceledException("video_purge_in_progress", token);
            var operation = new Operation(key, token);
            if (!Active.TryGetValue(key, out var operations)) Active[key] = operations = [];
            operations.Add(operation); return operation;
        }
    }

    public static async Task<IDisposable> BeginPurgeAsync(string path, CancellationToken token)
    {
        var key = Key(path); Operation[] operations;
        lock (Gate)
        {
            if (!Purging.Add(key)) throw new InvalidOperationException("video_purge_in_progress");
            operations = Active.TryGetValue(key, out var active) ? active.ToArray() : [];
        }
        try
        {
            foreach (var operation in operations)
                try { operation.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            await Task.WhenAll(operations.Select(operation => operation.Finished.Task)).WaitAsync(token).ConfigureAwait(false);
            return new PurgeLease(key);
        }
        catch { lock (Gate) Purging.Remove(key); throw; }
    }

    private sealed class PurgeLease(string key) : IDisposable
    {
        public void Dispose() { lock (Gate) Purging.Remove(key); }
    }
}
