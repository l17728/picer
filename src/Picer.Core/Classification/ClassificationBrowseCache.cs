using Picer.Core.Data;

namespace Picer.Core.Classification;

/// <summary>One thin replica per database, invalidated by every external commit.</summary>
public sealed class ClassificationBrowseCache : IDisposable
{
    private readonly PicerDatabase _reader;
    private readonly bool _owned;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClassificationReplica? _replica;
    private long _version = -1;
    private bool _disposed;

    public ClassificationBrowseCache(PicerDatabase database)
    {
        _owned = database.SupportsIndependentReadSnapshots;
        _reader = _owned ? database.OpenReadOnlyConnection() : database;
    }

    public ClassificationReplica ExportReplica(CancellationToken token = default)
    {
        _gate.Wait(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var before = DataVersion();
            if (!_owned || _replica is null || before != _version)
            {
                var snapshot = new ClassificationRepository(_reader).ExportReplica();
                var after = DataVersion();
                // A commit concurrent with capture cannot tag an older snapshot as current.
                _version = before == after ? after : -1;
                _replica = snapshot;
            }
            token.ThrowIfCancellationRequested();
            return _replica;
        }
        finally { _gate.Release(); }
    }

    public ClassificationBrowseResult Browse(ClassificationBrowseRequest request, CancellationToken token = default) =>
        ClassificationRepository.BrowseReplica(ExportReplica(token), request, cached: false);

    private long DataVersion()
    {
        using var query = _reader.Command("PRAGMA data_version");
        return Convert.ToInt64(query.ExecuteScalar());
    }

    public void Dispose()
    {
        _gate.Wait();
        try { if (_disposed) return; _disposed = true; if (_owned) _reader.Dispose(); _replica = null; }
        finally { _gate.Release(); }
    }
}
