using System.Collections.Concurrent;

namespace Picer.Core.Gateway;

/// <summary>Prevents transient thumbnail failures from turning viewport rendering into a retry loop.</summary>
public sealed class ThumbnailRetryTracker(TimeSpan? initialDelay = null, TimeSpan? maximumDelay = null)
{
    private sealed record Failure(int Count, DateTimeOffset RetryAt);
    private readonly ConcurrentDictionary<long, Failure> _failures = new();
    private readonly TimeSpan _initialDelay = initialDelay ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan _maximumDelay = maximumDelay ?? TimeSpan.FromSeconds(60);

    public bool CanRetry(long id, DateTimeOffset now) =>
        !_failures.TryGetValue(id, out var failure) || failure.RetryAt <= now;

    public DateTimeOffset? GetRetryAt(long id) =>
        _failures.TryGetValue(id, out var failure) ? failure.RetryAt : null;

    public TimeSpan RecordFailure(long id, DateTimeOffset now)
    {
        var failure = _failures.AddOrUpdate(id,
            _ => new Failure(1, now + _initialDelay),
            (_, previous) =>
            {
                var exponent = Math.Min(previous.Count, 20);
                var ticks = _initialDelay.Ticks > _maximumDelay.Ticks >> exponent
                    ? _maximumDelay.Ticks
                    : Math.Min(_maximumDelay.Ticks, _initialDelay.Ticks << exponent);
                return new Failure(previous.Count + 1, now + TimeSpan.FromTicks(ticks));
            });
        return failure.RetryAt - now;
    }

    public void RecordSuccess(long id) => _failures.TryRemove(id, out _);
}
