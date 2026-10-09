namespace Picer.Core.Indexing;

/// <summary>
/// Directory listing cache with optional persistent NTFS journal checkpoints. Unsupported
/// or discontinuous journals always fall back to complete enumeration after restart.
/// </summary>
public sealed class DirectoryMediaChangeTracker : IDisposable
{
    public sealed record Listing(string[] Files, DirectoryMediaFailure[] Failures, int EnumeratedDirectories, int ReusedDirectories, bool Complete,
        IReadOnlySet<string>? ChangedPaths = null, bool ReusedPersistentManifest = false, DirectoryJournalCheckpoint? Checkpoint = null,
        long Revision = 0, IReadOnlyDictionary<string, string>? VerifiedHashes = null);
    internal sealed record DirectoryListing(DateTime ModifiedUtc, string[] Files, string[] Children,
        ulong DirectoryReference = 0, Dictionary<string, ulong>? FileReferences = null);
    private sealed class RootState : IDisposable
    {
        public readonly Dictionary<string, DirectoryListing> Directories = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Dirty = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> PendingContent = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> VerifiedHashes = new(StringComparer.OrdinalIgnoreCase);
        public FileSystemWatcher? Watcher;
        public long Version;
        public bool Healthy = true;
        public DateTime LastUsed = DateTime.UtcNow;
        public DateTime LastFull = DateTime.MinValue;
        public DirectoryJournalCheckpoint? Checkpoint;
        public long Revision;
        public string Generation = "";
        public void Dispose() => Watcher?.Dispose();
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, RootState> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _maximumAge;
    private readonly Timer _timer;
    private bool _disposed;
    private long _revision;
    private readonly DirectoryScanManifestStore? _manifests;
    private readonly IDirectoryChangeJournal _journal;
    public static DirectoryMediaChangeTracker Shared { get; } = CreateShared();

    private static DirectoryMediaChangeTracker CreateShared()
    {
        var dataRoot = Environment.GetEnvironmentVariable("PICER_SCAN_CACHE_ROOT") ?? Path.Combine(
            Environment.GetEnvironmentVariable("PICER_DATA_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Picer"), "scan-manifests");
        var tracker = new DirectoryMediaChangeTracker(persistentCacheRoot: dataRoot);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => tracker.Dispose();
        return tracker;
    }

    public DirectoryMediaChangeTracker(TimeSpan? maximumAge = null, string? persistentCacheRoot = null, IDirectoryChangeJournal? journal = null)
    {
        _maximumAge = maximumAge ?? TimeSpan.FromMinutes(10);
        if (_maximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));
        _manifests = persistentCacheRoot is null ? null : new DirectoryScanManifestStore(persistentCacheRoot);
        _journal = journal ?? new NtfsDirectoryChangeJournal();
        _timer = new Timer(_ => RemoveIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void RemoveIdle()
    {
        lock (_gate)
        {
            foreach (var key in _roots.Where(pair => DateTime.UtcNow - pair.Value.LastUsed >= _maximumAge).Select(pair => pair.Key).ToArray())
            {
                _roots[key].Dispose();
                _roots.Remove(key);
            }
        }
    }

    /// <summary>Explicitly invalidate after a known monitoring gap or external storage reconnect.</summary>
    public void Invalidate(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        lock (_gate)
        {
            if (_roots.TryGetValue(root, out var state)) { state.Healthy = false; state.Version++; }
            _manifests?.Forget(root);
        }
    }

    /// <summary>Manual index purge must also erase cached paths/file IDs/content hashes.</summary>
    public void ForgetFile(string path)
    {
        lock (_gate)
        {
            foreach (var root in _roots.Keys.Where(root => DirectoryMediaImport.Contains(root, path)).ToArray())
            {
                var state = _roots[root];
                state.Healthy = false; state.Version++;
                state.Directories.Clear(); state.Dirty.Clear(); state.PendingContent.Clear(); state.VerifiedHashes.Clear(); state.Checkpoint = null;
                _manifests?.Forget(root);
            }
            _manifests?.ForgetFile(path);
        }
    }

    public Listing Read(string root, bool forceFull, Func<string, bool> includeFile, CancellationToken token)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        RootState state;
        Dictionary<string, DirectoryListing> cached;
        HashSet<string> dirty;
        long version;
        bool full;
        var journalStart = _manifests is null ? null : _journal.Capture(root);
        var generation = _manifests?.Generation() ?? "";
        var reusedPersistent = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var monitoringGap = _roots.TryGetValue(root, out var old) && (!old.Healthy || old.Generation != generation);
            if (monitoringGap && old is not null)
            { old.Dispose(); _roots.Remove(root); }
            if (!_roots.TryGetValue(root, out state!))
            {
                if (_roots.Count >= 4)
                {
                    var oldest = _roots.MinBy(pair => pair.Value.LastUsed);
                    oldest.Value.Dispose(); _roots.Remove(oldest.Key);
                }
                state = new RootState { Generation = generation };
                _roots.Add(root, state);
                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 32 * 1024
                    };
                    state.Watcher = watcher;
                    var captured = state;
                    watcher.Created += (_, e) => Changed(captured, e.FullPath);
                    watcher.Deleted += (_, e) => Changed(captured, e.FullPath);
                    watcher.Changed += (_, e) => Changed(captured, e.FullPath);
                    watcher.Renamed += (_, e) => { Changed(captured, e.OldFullPath); Changed(captured, e.FullPath); };
                    watcher.Error += (_, _) => { lock (_gate) { captured.Healthy = false; captured.Version++; } };
                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
                { state.Healthy = false; state.Dispose(); }
                if (!forceFull && !monitoringGap && journalStart is not null && _manifests?.Load(root) is { } manifest
                    && NtfsDirectoryChangeJournal.IsContinuous(manifest.Checkpoint, journalStart))
                {
                    foreach (var pair in manifest.Directories) state.Directories[pair.Key] = pair.Value;
                    state.Checkpoint = manifest.Checkpoint;
                    state.PendingContent.UnionWith(manifest.PendingFiles);
                    foreach (var pair in manifest.VerifiedHashes) state.VerifiedHashes[pair.Key] = pair.Value;
                    state.LastFull = manifest.SavedUtc;
                    reusedPersistent = true;
                }
            }
            var journalVerified = false;
            if (!forceFull && state.Checkpoint is { } previous && journalStart is not null)
            {
                var delta = ReadChanges(root, previous, journalStart, state.Directories, token);
                journalVerified = delta.Reliable;
                if (delta.Reliable) { state.Dirty.UnionWith(delta.DirtyDirectories); state.Dirty.UnionWith(delta.DirtyFiles); }
                else reusedPersistent = false;
            }
            state.LastUsed = DateTime.UtcNow;
            full = forceFull || !state.Healthy || (state.Checkpoint is not null && !journalVerified)
                || (!journalVerified && DateTime.UtcNow - state.LastFull >= _maximumAge);
            if (full) reusedPersistent = false;
            cached = full ? new(StringComparer.OrdinalIgnoreCase) : new(state.Directories, StringComparer.OrdinalIgnoreCase);
            dirty = new(state.Dirty, StringComparer.OrdinalIgnoreCase);
            dirty.UnionWith(state.PendingContent);
            version = state.Version;
        }

        var files = new List<string>();
        var failures = new List<DirectoryMediaFailure>();
        var next = new Dictionary<string, DirectoryListing>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(root);
        var enumerated = 0; var reused = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                { failures.Add(new(directory, "directory_link_not_followed", true)); continue; }
                var modified = info.LastWriteTimeUtc;
                DirectoryListing listing;
                if (!dirty.Contains(directory) && cached.TryGetValue(directory, out var previous) && previous.ModifiedUtc == modified)
                { listing = previous; reused++; }
                else
                {
                    enumerated++;
                    var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
                    var foundFiles = new List<string>();
                    foreach (var file in Directory.EnumerateFiles(directory, "*", options))
                    { token.ThrowIfCancellationRequested(); if (includeFile(file)) foundFiles.Add(file); }
                    var children = new List<string>();
                    foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
                    { token.ThrowIfCancellationRequested(); if (!Path.GetFileName(child).Equals(".picer-keyframes", StringComparison.OrdinalIgnoreCase)) children.Add(child); }
                    info.Refresh();
                    if (info.LastWriteTimeUtc != modified) failures.Add(new(directory, "directory_changed_during_enumeration", true));
                    listing = new(modified, foundFiles.ToArray(), children.ToArray());
                }
                next[directory] = listing;
                if (journalStart is not null && (listing.DirectoryReference == 0 || listing.FileReferences is null))
                {
                    var references = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in listing.Files)
                    {
                        token.ThrowIfCancellationRequested();
                        if (_journal.GetReference(file) is { } reference) references[file] = reference;
                    }
                    next[directory] = listing with { DirectoryReference = _journal.GetReference(directory) ?? 0, FileReferences = references };
                }
                files.AddRange(listing.Files);
                foreach (var child in listing.Children) pending.Push(child);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { failures.Add(new(directory, exception.GetType().Name, true)); }
        }
        token.ThrowIfCancellationRequested();
        // If no continuous durable watermark supports this full walk, size/mtime
        // equality alone cannot prove that indexed content is unchanged.
        if (full && _manifests is not null) dirty.UnionWith(files);
        DirectoryJournalCheckpoint? journalEnd = null;
        var journalStable = true;
        if (journalStart is not null && next.Values.All(value => value.DirectoryReference != 0 && value.FileReferences?.Count == value.Files.Length))
        {
            journalEnd = _journal.Capture(root);
            var tail = journalEnd is null ? new DirectoryJournalDelta(false, [], []) : ReadChanges(root, journalStart, journalEnd, next, token);
            journalStable = tail.Reliable && tail.DirtyDirectories.Count == 0 && tail.DirtyFiles.Count == 0;
        }
        bool complete;
        DirectoryScanManifest? saved = null;
        var revision = 0L;
        Dictionary<string, string> verifiedHashes;
        lock (_gate)
        {
            // Do not publish a partial/canceled listing or clear concurrent change notifications.
            var retained = !_disposed && _roots.TryGetValue(root, out var current) && ReferenceEquals(current, state);
            complete = retained && state.Version == version && failures.Count == 0 && journalStable
                && generation == (_manifests?.Generation() ?? "");
            if (complete && state.Healthy)
            {
                state.Directories.Clear();
                foreach (var pair in next) state.Directories.Add(pair.Key, pair.Value);
                state.Dirty.Clear();
                state.PendingContent.UnionWith(dirty.Where(includeFile).Select(DirectoryMediaImport.Normalize));
                state.Checkpoint = journalEnd;
                revision = state.Revision = Interlocked.Increment(ref _revision);
                if (full) state.LastFull = DateTime.UtcNow;
                if (journalEnd is not null) saved = Manifest(root, state);
            }
            verifiedHashes = new(state.VerifiedHashes, StringComparer.OrdinalIgnoreCase);
        }
        if (saved is not null) _manifests?.Save(saved);
        return new(files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), failures.ToArray(), enumerated, reused, complete,
            dirty.Select(DirectoryMediaImport.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase), reusedPersistent, journalEnd, revision, verifiedHashes);
    }

    /// <summary>Only successfully re-read media can clear a persisted content-change obligation.</summary>
    public void AcknowledgeContent(string root, DirectoryJournalCheckpoint? checkpoint, long revision,
        IEnumerable<DirectoryMediaEntry> verified, IEnumerable<string> absent)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        lock (_gate)
        {
            if (!_roots.TryGetValue(root, out var state) || state.Checkpoint != checkpoint || state.Revision != revision || !state.Healthy) return;
            var removed = false;
            foreach (var entry in verified)
            {
                var path = DirectoryMediaImport.Normalize(entry.Path);
                removed |= state.PendingContent.Remove(path);
                if (entry.Sha256 is { Length: 64 } hash)
                { removed |= !state.VerifiedHashes.TryGetValue(path, out var oldHash) || oldHash != hash; state.VerifiedHashes[path] = hash; }
            }
            foreach (var path in absent)
            {
                var normalized = DirectoryMediaImport.Normalize(path);
                removed |= state.PendingContent.Remove(normalized);
                state.VerifiedHashes.Remove(normalized);
            }
            if (removed && checkpoint is not null) _manifests?.Save(Manifest(root, state));
        }
    }

    private static DirectoryScanManifest Manifest(string root, RootState state) => new(1, root, state.Checkpoint!, DateTime.UtcNow,
        new(state.Directories, StringComparer.OrdinalIgnoreCase), state.PendingContent.ToArray(), new(state.VerifiedHashes, StringComparer.OrdinalIgnoreCase), state.Generation);

    private DirectoryJournalDelta ReadChanges(string root, DirectoryJournalCheckpoint from, DirectoryJournalCheckpoint to,
        IReadOnlyDictionary<string, DirectoryListing> entries, CancellationToken token)
    {
        if (!NtfsDirectoryChangeJournal.IsContinuous(from, to)) return new(false, [], []);
        try
        {
            var directories = entries.ToDictionary(pair => pair.Key, pair => pair.Value.DirectoryReference, StringComparer.OrdinalIgnoreCase);
            var files = entries.Values.SelectMany(value => value.FileReferences ?? []).GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
            return _journal.ReadChanges(root, from, to, directories, files, token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return new(false, [], []); }
    }

    private void Changed(RootState state, string path)
    {
        lock (_gate)
        {
            state.Version++;
            state.Dirty.Add(path);
            if (Path.GetDirectoryName(path) is { } parent) state.Dirty.Add(parent);
            // Bounded memory even when no scan consumes a long-running change stream.
            if (state.Dirty.Count > 8192) { state.Healthy = false; state.Dirty.Clear(); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            foreach (var state in _roots.Values) state.Dispose();
            _roots.Clear();
        }
    }
}
