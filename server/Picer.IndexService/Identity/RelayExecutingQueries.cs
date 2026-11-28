namespace Picer.IndexService.Identity;

/// <summary>Owned by one session reader. Request cancellation never cancels a sibling operation.</summary>
internal sealed class RelayExecutingQueries(CancellationToken sessionToken, int capacity = 32) : IAsyncDisposable
{
    private sealed record Execution(Guid Operation, CancellationTokenSource Stop, Task Work);
    private readonly Dictionary<Guid, Execution> _active = [];
    private readonly CancellationTokenSource _session = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
    public CancellationToken SessionToken => _session.Token;

    public bool TryStart(Guid request, Guid operation, DateTimeOffset expires, Func<CancellationToken, Task> execute)
    {
        foreach (var entry in _active.Where(e => e.Value.Work.IsCompleted).ToArray())
        {
            _active.Remove(entry.Key);
            entry.Value.Stop.Dispose();
            entry.Value.Work.GetAwaiter().GetResult();
        }
        if (_active.ContainsKey(request)) throw new InvalidDataException("relay_request_already_active");
        if (_active.Count >= capacity) return false;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(SessionToken);
        var remaining = expires - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) stop.Cancel();
        else stop.CancelAfter(remaining);
        var work = Task.Run(async () =>
        {
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                await execute(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch { await _session.CancelAsync().ConfigureAwait(false); throw; }
        });
        _active.Add(request, new(operation, stop, work));
        return true;
    }

    public async Task CancelAsync(Guid request, Guid operation)
    {
        if (!_active.TryGetValue(request, out var execution)) return;
        if (execution.Operation != operation) throw new InvalidDataException("relay_cancel_binding_invalid");
        await execution.Stop.CancelAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _session.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(_active.Values.Select(e => e.Work)).ConfigureAwait(false); }
        finally
        {
            foreach (var execution in _active.Values) execution.Stop.Dispose();
            _active.Clear();
            _session.Dispose();
        }
    }
}
