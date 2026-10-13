using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.People;
using Picer.Core.Search;

namespace Picer.IndexService.Identity;

/// <summary>Bounded, grant-bound immutable read cuts. No snapshot database is exposed to clients.</summary>
public sealed class SpaceReadSnapshots : IDisposable
{
    private sealed record Entry(string Space, string Grant, string Name, string Epoch, DateTimeOffset Expires);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly string _root;
    private readonly Timer _expiry;
    private bool _disposed;
    public SpaceReadSnapshots(AccountStore accounts)
    {
        _root = Path.GetFullPath(Path.Combine(accounts.Root, "read-snapshots")); Directory.CreateDirectory(_root);
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("invalid_snapshot_cache_root");
        foreach (var file in Directory.EnumerateFiles(_root)) DeleteOwned(file);
        _expiry = new(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public GatewayChangePage ReadChanges(PicerDatabase live, AuthorizedDevice device, GatewayChangeRequest request, Func<bool>? unavailable = null)
    {
        using (Enter())
        {
            ObjectDisposedException.ThrowIf(_disposed, this); SweepLocked();
            if (unavailable?.Invoke() == true) throw new SpaceCapacityException();
            using (var state = live.Command("SELECT epoch,revision,metadata_revision FROM mirror_revision WHERE id=1 AND EXISTS(SELECT 1 FROM sync_space_state WHERE id=1 AND state='active')"))
            using (var row = state.ExecuteReader())
            {
                if (!row.Read()) throw new InvalidOperationException("read_snapshot_expired");
                if (request.Through is null && request.Epoch == row.GetString(0) && request.After == row.GetInt64(1) && request.MetadataRevision == row.GetInt64(2))
                    return new(row.GetString(0), row.GetInt64(1), row.GetInt64(1), false, false, row.GetInt64(2), []);
            }
            Entry entry;
            if (request.Snapshot is { } name) entry = Find(live, device, name);
            else
            {
                if (request.Through is not null) throw new InvalidOperationException("read_snapshot_required");
                foreach (var previous in _entries.Values.Where(item => item.Space == device.SpaceId && item.Grant == device.GrantId).ToArray()) Remove(previous);
                if (_entries.Count >= 8) throw new SpaceCapacityException();
                name = Guid.NewGuid().ToString("N");
                var path = Path.Combine(_root, name + ".db");
                var drive = new DriveInfo(Path.GetPathRoot(_root)!);
                using var pages = live.Command("SELECT page_count*page_size FROM pragma_page_count(),pragma_page_size()");
                var bytes = Convert.ToInt64(pages.ExecuteScalar());
                if (drive.AvailableFreeSpace < bytes + 2L * 1024 * 1024 * 1024) throw new SpaceCapacityException();
                try
                {
                    live.BackupTo(path);
                    using var snapshot = new PicerDatabase(path);
                    using var epoch = snapshot.Command("SELECT epoch FROM mirror_revision WHERE id=1");
                    entry = new(device.SpaceId, device.GrantId, name, (string)epoch.ExecuteScalar()!, DateTimeOffset.UtcNow.AddMinutes(15));
                    _entries.Add(name, entry);
                }
                catch { DeleteFiles(name); throw; }
            }
            using var database = new PicerDatabase(Path.Combine(_root, entry.Name + ".db"));
            var page = database.ReadMirrorChanges(request);
            return page with { Snapshot = entry.Name, Sources = request.Through is null ? database.GetSources() : null };
        }
    }

    public object ReadCatalog(PicerDatabase live, AuthorizedDevice device, string name, string path, bool embeddings, bool models)
    {
        using (Enter())
        {
            var entry = Find(live, device, name);
            using var snapshot = new PicerDatabase(Path.Combine(_root, entry.Name + ".db"));
            return path switch
            {
                "/api/gateway/v1/tags" => SemanticEngine.GetTags(snapshot),
                "/api/gateway/v1/albums" => SemanticEngine.GetAlbums(snapshot),
                "/api/gateway/v1/people" => new PeopleService(snapshot).GetAll(),
                "/api/gateway/v1/metadata" => IndexApiHandlers.ReadMetadata(snapshot, embeddings, models),
                _ => throw new ArgumentException("invalid_snapshot_route")
            };
        }
    }

    private Entry Find(PicerDatabase live, AuthorizedDevice device, string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.TryGetValue(name, out var entry) || entry.Space != device.SpaceId || entry.Grant != device.GrantId)
            throw new InvalidOperationException("read_snapshot_expired");
        using var epoch = live.Command("SELECT epoch FROM mirror_revision WHERE id=1 AND EXISTS(SELECT 1 FROM sync_space_state WHERE id=1 AND state='active')");
        if (entry.Expires <= DateTimeOffset.UtcNow || epoch.ExecuteScalar() as string != entry.Epoch)
        { Remove(entry); throw new InvalidOperationException("read_snapshot_expired"); }
        entry = entry with { Expires = DateTimeOffset.UtcNow.AddMinutes(15) };
        _entries[name] = entry;
        return entry;
    }
    public void Invalidate(string space)
    { using (Enter()) foreach (var item in _entries.Values.Where(item => item.Space == space).ToArray()) Remove(item); }
    public void Release(AuthorizedDevice device, string name)
    {
        using (Enter())
            if (_entries.TryGetValue(name, out var entry) && entry.Space == device.SpaceId && entry.Grant == device.GrantId) Remove(entry);
    }
    private IDisposable Enter()
    {
        if (!Monitor.TryEnter(_gate, TimeSpan.FromSeconds(2))) throw new SpaceCapacityException();
        return new GateLease(_gate);
    }
    private sealed class GateLease(object gate) : IDisposable { public void Dispose() => Monitor.Exit(gate); }
    private void Sweep()
    {
        try { lock (_gate) { if (!_disposed) SweepLocked(); } }
        catch (IOException) { /* Retain failed cleanup entries and retry at the next sweep. */ }
        catch (UnauthorizedAccessException) { }
    }
    private void SweepLocked()
    { foreach (var item in _entries.Values.Where(item => item.Expires <= DateTimeOffset.UtcNow).ToArray()) Remove(item); }
    private void Remove(Entry entry) { DeleteFiles(entry.Name); _entries.Remove(entry.Name); }
    private void DeleteFiles(string name)
    { foreach (var suffix in new[] { ".db", ".db-wal", ".db-shm", ".db.access" }) DeleteOwned(Path.Combine(_root, name + suffix)); }
    private void DeleteOwned(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) != _root || (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)))
            throw new IOException("invalid_snapshot_cache_path");
        File.Delete(path);
    }
    public void Dispose()
    {
        _expiry.Dispose();
        lock (_gate) { if (_disposed) return; _disposed = true; foreach (var entry in _entries.Values.ToArray()) Remove(entry); }
    }
}
