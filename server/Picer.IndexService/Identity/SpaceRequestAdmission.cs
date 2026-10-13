namespace Picer.IndexService.Identity;

/// <summary>Independent budgets keep long media sockets from exhausting metadata and foreground index operations.</summary>
public sealed class SpaceRequestAdmission
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public IDisposable? TryEnter(AuthorizedDevice device, string path)
    {
        var category = path.StartsWith("/api/agent/", StringComparison.Ordinal) ? "agent"
            : path.Contains("/assets/", StringComparison.Ordinal) ? "media"
            : path.StartsWith("/api/sync/", StringComparison.Ordinal) ? "write" : "index";
        var limits = category switch
        {
            "agent" => (Node: 128, Space: 32, Device: 10),
            "media" => (Node: 32, Space: 8, Device: 4),
            "write" => (Node: 8, Space: 2, Device: 1),
            _ => (Node: 64, Space: 12, Device: 6)
        };
        var keys = new[] { category, category + ":s:" + device.SpaceId, category + ":d:" + device.GrantId };
        lock (_gate)
        {
            if (_counts.GetValueOrDefault(keys[0]) >= limits.Node || _counts.GetValueOrDefault(keys[1]) >= limits.Space ||
                _counts.GetValueOrDefault(keys[2]) >= limits.Device) return null;
            foreach (var key in keys) _counts[key] = _counts.GetValueOrDefault(key) + 1;
        }
        return new Lease(this, keys);
    }

    private sealed class Lease(SpaceRequestAdmission owner, string[] keys) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate)
                foreach (var key in keys)
                {
                    var remaining = owner._counts[key] - 1;
                    if (remaining == 0) owner._counts.Remove(key); else owner._counts[key] = remaining;
                }
        }
    }
}
