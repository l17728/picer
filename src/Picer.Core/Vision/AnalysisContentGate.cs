namespace Picer.Core.Vision;

/// <summary>Shares only active work across service instances; idle keys retain no media identity.</summary>
internal static class AnalysisContentGate
{
    private static readonly object Sync = new();
    private static readonly Dictionary<(string Database, string Content), Entry> Entries = [];
    private sealed class Entry { public readonly SemaphoreSlim Gate = new(1, 1); public int Users; }

    public static async Task<IDisposable> EnterAsync(string database, string content, CancellationToken token)
    {
        var key = (OperatingSystem.IsWindows() ? database.ToUpperInvariant() : database, content);
        Entry entry;
        lock (Sync)
        {
            if (!Entries.TryGetValue(key, out entry!)) Entries[key] = entry = new();
            entry.Users++;
        }
        try { await entry.Gate.WaitAsync(token).ConfigureAwait(false); }
        catch { Release(key, entry, false); throw; }
        return new Lease(key, entry);
    }

    private static void Release((string Database, string Content) key, Entry entry, bool entered)
    {
        if (entered) entry.Gate.Release();
        lock (Sync)
            if (--entry.Users == 0) { Entries.Remove(key); entry.Gate.Dispose(); }
    }

    private sealed class Lease((string Database, string Content) key, Entry entry) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Release(key, entry, true);
        }
    }
}
