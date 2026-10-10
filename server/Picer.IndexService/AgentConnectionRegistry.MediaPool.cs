using System.Net.WebSockets;
using Picer.Core.Gateway;

namespace Picer.IndexService;

internal sealed partial class AgentConnectionRegistry
{
    private sealed class ReusableMedia(AgentSession owner, string path, WebSocket socket,
        MediaWire.Metadata metadata, long generation, Action close)
    {
        public readonly AgentSession Owner = owner;
        public readonly string Path = path;
        public readonly WebSocket Socket = socket;
        public readonly MediaWire.Metadata Metadata = metadata;
        public readonly long Generation = generation;
        public bool InUse = true;
        public DateTime IdleSince;
        private int _closed;
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { Socket.Abort(); Socket.Dispose(); } finally { close(); }
        }
    }

    private readonly object _mediaPoolLock = new();
    private readonly HashSet<ReusableMedia> _mediaPool = [];
    private readonly Timer _mediaExpiry;
    private long _mediaGeneration;
    private int _mediaWaiters;
    private bool _mediaStopped;

    private ReusableMedia? TakeMedia(AgentSession session, string path, string? expected)
    {
        if (expected is null) return null;
        lock (_mediaPoolLock)
        {
            var entry = _mediaPool.FirstOrDefault(item => !item.InUse && ReferenceEquals(item.Owner, session)
                && item.Path.Equals(path, StringComparison.Ordinal) && item.Metadata.ETag == expected);
            if (entry is not null) entry.InUse = true;
            return entry;
        }
    }

    private void RetireMedia(ReusableMedia entry)
    {
        lock (_mediaPoolLock) _mediaPool.Remove(entry);
        entry.Close();
    }

    private bool ReturnMedia(ReusableMedia entry)
    {
        lock (_mediaPoolLock)
        {
            if (!_mediaStopped && entry.Generation == _mediaGeneration && _mediaPool.Contains(entry)
                && Volatile.Read(ref _mediaWaiters) == 0 && entry.Socket.State == WebSocketState.Open
                && _sessions.TryGetValue(entry.Owner.AgentId, out var current) && ReferenceEquals(current, entry.Owner))
            {
                entry.InUse = false; entry.IdleSince = DateTime.UtcNow; return true;
            }
        }
        RetireMedia(entry); return false;
    }

    private SocketMediaStream BorrowMedia(ReusableMedia entry)
    {
        var returned = false;
        return new(entry.Socket, entry.Metadata, () => { if (!returned) RetireMedia(entry); },
            _ => returned = ReturnMedia(entry));
    }

    private void EvictIdleMedia()
    {
        ReusableMedia? entry;
        lock (_mediaPoolLock)
        {
            entry = _mediaPool.Where(item => !item.InUse).MinBy(item => item.IdleSince);
            if (entry is not null) _mediaPool.Remove(entry);
        }
        entry?.Close();
    }

    private void SweepMedia()
    {
        ReusableMedia[] expired;
        lock (_mediaPoolLock)
        {
            expired = _mediaPool.Where(item => !item.InUse && DateTime.UtcNow - item.IdleSince > TimeSpan.FromSeconds(10)).ToArray();
            foreach (var entry in expired) _mediaPool.Remove(entry);
        }
        foreach (var entry in expired) entry.Close();
    }

    private void ClearPooledMedia(AgentSession? owner = null, string? etag = null, bool stopping = false)
    {
        ReusableMedia[] closed;
        lock (_mediaPoolLock)
        {
            // Also prevent handshakes that began before invalidation from entering the pool.
            _mediaGeneration++;
            _mediaStopped |= stopping;
            closed = _mediaPool.Where(item => (owner is null || ReferenceEquals(item.Owner, owner))
                && (etag is null || item.Metadata.ETag == etag)).ToArray();
            foreach (var entry in closed) _mediaPool.Remove(entry);
        }
        foreach (var entry in closed) entry.Close();
    }

    public void StopMediaConnections()
    {
        _mediaExpiry.Dispose();
        ClearPooledMedia(stopping: true);
    }

    private static MediaWire.Metadata ValidateMediaMetadata(MediaWire.Metadata metadata)
    {
        if (metadata.Error == "media_source_changed") throw new MediaSourceChangedException();
        if (metadata.Error is not null || metadata.Length < 0 || metadata.ETag.Length is < 3 or > 128)
            throw new IOException("media_source_unavailable");
        return metadata;
    }
}
