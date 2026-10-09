using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Picer.Core.Indexing;

public sealed record DirectoryMediaScanRequest(long SourceId, string Directory, string? Snapshot = null, int Offset = 0, int Limit = 100,
    bool ForceFullScan = false, IReadOnlyList<DirectoryMediaEntry>? KnownEntries = null);
public sealed record DirectoryMediaEntry(string Path, long FileSize, DateTime ModifiedUtc, bool IsVideo, string? Sha256, DirectoryMediaMetadata? Metadata = null, bool Reused = false);
public sealed record DirectoryMediaFailure(string Path, string Code, bool IsDirectory = false);
public sealed record DirectoryMediaPage(string Snapshot, int Total, int NextOffset, bool Done, IReadOnlyList<DirectoryMediaEntry> Items,
    int StartOffset = 0, IReadOnlyList<string>? ObservedPaths = null, IReadOnlyList<DirectoryMediaFailure>? Failures = null, bool EnumerationComplete = false,
    bool IndexChanged = true);

/// <summary>Read-only, paginated source-machine enumeration; never follows directory junctions.</summary>
public sealed class DirectoryMediaScanner(Func<string, bool, CancellationToken, Task<DirectoryMediaMetadata>>? metadataReader = null,
    DirectoryMediaChangeTracker? changeTracker = null)
{
    private sealed record Snapshot(string Root, string[] Files, DirectoryMediaFailure[] Failures, bool EnumerationComplete, DateTime CreatedUtc,
        IReadOnlyDictionary<string, DirectoryMediaEntry> Known, IReadOnlyDictionary<string, DirectoryMediaEntry> Indexed,
        IReadOnlySet<string> ChangedPaths, DirectoryJournalCheckpoint? Checkpoint, long Revision)
    {
        public ConcurrentDictionary<string, DirectoryMediaEntry> Verified { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private readonly DirectoryMediaChangeTracker _changeTracker = changeTracker ?? DirectoryMediaChangeTracker.Shared;
    private readonly Func<string, bool, CancellationToken, Task<DirectoryMediaMetadata>> _metadataReader = metadataReader
        ?? ((path, video, token) => DirectoryMediaMetadataReader.ReadAsync(path, video, token));
    private readonly ConcurrentDictionary<string, Snapshot> _snapshots = new();
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".wmv", ".mts", ".m2ts", ".3gp", ".webm" };

    public async Task<DirectoryMediaPage> ReadAsync(DirectoryMediaScanRequest request, CancellationToken token = default)
    {
        if (request.Offset < 0 || request.Limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(request));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Directory));
        for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("directory_link_not_allowed");
        foreach (var expired in _snapshots.Where(pair => pair.Value.CreatedUtc < DateTime.UtcNow.AddHours(-2))) _snapshots.TryRemove(expired.Key, out _);
        var id = request.Snapshot;
        if (id is null)
        {
            if (request.Offset != 0) throw new ArgumentException("missing_directory_snapshot");
            if (_snapshots.Count >= 4)
                _snapshots.TryRemove(_snapshots.MinBy(pair => pair.Value.CreatedUtc).Key, out _);
            var listing = _changeTracker.Read(root, request.ForceFullScan,
                file => SupportedFormats.IsSupported(file) || Videos.Contains(Path.GetExtension(file)), token);
            id = Guid.NewGuid().ToString("N");
            var indexed = (request.KnownEntries ?? []).Where(entry => DirectoryMediaImport.Contains(root, entry.Path))
                    .GroupBy(entry => DirectoryMediaImport.Normalize(entry.Path), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var known = request.ForceFullScan ? new Dictionary<string, DirectoryMediaEntry>() : indexed
                .Where(pair => listing.ChangedPaths?.Contains(pair.Key) != true
                    && (listing.VerifiedHashes is null || !listing.VerifiedHashes.TryGetValue(pair.Key, out var hash) || hash == pair.Value.Sha256))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            _snapshots[id] = new(root, listing.Files, listing.Failures, listing.Complete, DateTime.UtcNow, known, indexed,
                listing.ChangedPaths ?? new HashSet<string>(), listing.Checkpoint, listing.Revision);
        }
        if (!_snapshots.TryGetValue(id, out var snapshot) || !string.Equals(snapshot.Root, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("directory_snapshot_expired_or_mismatched");
        if (request.Offset > snapshot.Files.Length) throw new ArgumentOutOfRangeException(nameof(request));
        var entries = new List<DirectoryMediaEntry>();
        var errors = new List<DirectoryMediaFailure>(snapshot.Failures);
        var observed = new List<string>();
        var expensive = 0;
        foreach (var file in snapshot.Files.Skip(request.Offset).Take(request.Limit))
        {
            token.ThrowIfCancellationRequested();
            try
            {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("media_replaced_with_link");
            var size = info.Length; var modified = info.LastWriteTimeUtc;
            var video = Videos.Contains(info.Extension);
            if (snapshot.Known.TryGetValue(DirectoryMediaImport.Normalize(file), out var previous)
                && previous.IsVideo == video && previous.FileSize == size && previous.ModifiedUtc == modified
                && previous.Metadata is { Width: > 0, Height: > 0 } && previous.Sha256 is { Length: 64 } previousHash && previousHash.All(Uri.IsHexDigit))
            {
                observed.Add(file);
                entries.Add(previous with { Path = file, Reused = true });
                continue;
            }
            // Bound slow decodes/probes per remote page while returning many unchanged entries at once.
            if (expensive >= 5) break;
            expensive++;
            observed.Add(file);
            string hash;
            {
                await using var stream = File.OpenRead(file);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
            }
            var metadata = await _metadataReader(file, video, token);
            info.Refresh();
            if (info.Length != size || info.LastWriteTimeUtc != modified) throw new IOException("media_changed_during_directory_scan");
            entries.Add(new(file, size, modified, video, hash, metadata));
            // Returning a page does not prove the caller committed it. Clear a pending
            // journal change only when a fresh read already agrees with the supplied
            // durable index signature; changed files otherwise get one extra verified
            // pass on the next run, including after cancellation or process restart.
            var normalized = DirectoryMediaImport.Normalize(file);
            if (snapshot.Indexed.TryGetValue(normalized, out var indexed) && indexed.Sha256 == hash && indexed.FileSize == size && indexed.ModifiedUtc == modified)
                snapshot.Verified[normalized] = entries[^1];
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { if (!observed.Contains(file)) observed.Add(file); errors.Add(new(file, "metadata_timeout")); }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            { if (!observed.Contains(file)) observed.Add(file); errors.Add(new(file, exception.GetType().Name)); }
        }
        var next = request.Offset + observed.Count;
        var done = next == snapshot.Files.Length;
        if (done)
        {
            _snapshots.TryRemove(id, out _);
            if (snapshot.EnumerationComplete && snapshot.Revision > 0)
            {
                var present = snapshot.Files.Select(DirectoryMediaImport.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _changeTracker.AcknowledgeContent(root, snapshot.Checkpoint, snapshot.Revision,
                    snapshot.Verified.Values, snapshot.ChangedPaths.Where(path => !present.Contains(path)));
            }
        }
        return new(id, snapshot.Files.Length, next, done, entries, request.Offset, observed, errors, snapshot.EnumerationComplete);
    }
}
