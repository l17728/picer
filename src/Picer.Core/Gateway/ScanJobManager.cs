using System.Collections.Concurrent;
using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Models;

namespace Picer.Core.Gateway;

public sealed class ScanJobManager
{
    private sealed class JobEntry
    {
        public required CancellationTokenSource Cancellation { get; init; }
        public required long SourceId { get; init; }
        public IndexProgress? Latest;
        public bool Done;
        public string? Error;
    }

    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();

    public string Start(IndexEngine engine, long sourceId, string sourcePath)
    {
        var jobId = "scan-" + Guid.NewGuid().ToString("N")[..12];
        var entry = new JobEntry { Cancellation = new CancellationTokenSource(), SourceId = sourceId };
        _jobs[jobId] = entry;
        var progress = new Progress<IndexProgress>(p => entry.Latest = p);
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.IndexSourceAsync(new SourceFolder { Id = sourceId, Path = sourcePath }, progress, entry.Cancellation.Token);
                entry.Done = true;
            }
            catch (OperationCanceledException)
            {
                entry.Error = "canceled";
                entry.Done = true;
            }
            catch (Exception exception)
            {
                entry.Error = exception.Message;
                entry.Done = true;
            }
        }, CancellationToken.None);
        return jobId;
    }

    public ScanJobInfo? Get(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry)) return null;
        var latest = entry.Latest;
        return new ScanJobInfo(jobId, entry.SourceId, latest?.Phase ?? "queued", latest?.Done ?? 0, latest?.Total ?? 0, entry.Done, entry.Error);
    }

    public bool Cancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry) || entry.Done) return false;
        entry.Cancellation.Cancel();
        return true;
    }
}
