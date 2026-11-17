using System.Diagnostics;
using System.Text.Json;
using Picer.Core.Indexing;
using Picer.Core.SemanticSearch;

internal static class DescriptionStressBenchmark
{
    internal static async Task RunAsync(int seconds)
    {
        if (seconds is < 30 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        var cache = new QueryEmbeddingCache();
        var watch = Stopwatch.StartNew();
        var process = Process.GetCurrentProcess();
        var completed = 0; var active = 0; var maximum = 0; var requested = 0L;
        using (var worker = new CoalescingSyncWorker(async token =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            try { await Task.Delay(100, token); Interlocked.Increment(ref completed); }
            finally { Interlocked.Decrement(ref active); }
        }))
        {
            var nextSample = TimeSpan.Zero;
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                for (var i = 0; i < 100; i++) { worker.Request(); requested++; }
                cache.Store("isolated-stress", "fixture-model", requested.ToString(), new float[4096]);
                if (watch.Elapsed >= nextSample)
                {
                    process.Refresh();
                    Console.WriteLine(JsonSerializer.Serialize(new { elapsedSeconds = watch.Elapsed.TotalSeconds,
                        requested, completed, active, maximum, privateBytes = process.PrivateMemorySize64,
                        managedBytes = GC.GetTotalMemory(false), handles = process.HandleCount }));
                    nextSample = watch.Elapsed + TimeSpan.FromSeconds(10);
                }
                await Task.Delay(100);
            }
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new { terminal = true, elapsedSeconds = watch.Elapsed.TotalSeconds,
            requested, completed, active, maximum, privateBytes = process.PrivateMemorySize64,
            managedBytes = GC.GetTotalMemory(false), handles = process.HandleCount }));
        if (maximum != 1 || active != 0) throw new InvalidOperationException("stress_worker_lifetime_invalid");
    }
}
