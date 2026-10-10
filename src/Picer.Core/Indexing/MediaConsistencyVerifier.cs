using System.Security.Cryptography;
using Picer.Core.Gateway;
using Picer.Core.Video;

namespace Picer.Core.Indexing;

public sealed record MediaConsistencyCandidate(long Id, long SourceId, string Root, string Path,
    long FileSize, DateTime ModifiedUtc, string? Sha256, bool IsVideo);
public sealed record MediaConsistencyRequest(IReadOnlyList<MediaConsistencyCandidate> Items, int AuditIndex = -1);
public sealed record MediaConsistencyResult(long Id, bool IsVideo, string State, DirectoryMediaEntry? Entry = null, string? Error = null);
public sealed record MediaConsistencyBatch(IReadOnlyList<MediaConsistencyResult> Items, bool Deferred = false);

/// <summary>Bounded read-only checks of existing references. Never enumerates or imports a whole subtree.</summary>
public sealed class MediaConsistencyVerifier(Func<string, bool, CancellationToken, Task<DirectoryMediaMetadata>>? metadataReader = null)
{
    public const string Capability = "directory.verify.v1";
    private readonly Func<string, bool, CancellationToken, Task<DirectoryMediaMetadata>> _metadata = metadataReader ?? ((path, video, token) => DirectoryMediaMetadataReader.ReadAsync(path, video, token));

    public async Task<MediaConsistencyBatch> VerifyAsync(MediaConsistencyRequest request, IReadOnlyList<string> roots, CancellationToken token,
        TimeSpan? budget = null)
    {
        if (request.Items.Count > 129 || request.AuditIndex < -1 || request.AuditIndex >= request.Items.Count)
            throw new ArgumentOutOfRangeException(nameof(request));
        using var passDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        passDeadline.CancelAfter(budget ?? TimeSpan.FromSeconds(45));
        token = passDeadline.Token;
        var results = new List<MediaConsistencyResult>();
        for (var index = 0; index < request.Items.Count; index++)
        {
            if (token.IsCancellationRequested) return new(results, Deferred: true);
            var item = request.Items[index];
            try
            {
                using var operation = VideoOperationLifetime.Begin(item.Path, token);
                var path = MediaSourceIdentity.ResolveAllowed(item.Path, roots);
                if (!DirectoryMediaImport.Contains(item.Root, path)) throw new UnauthorizedAccessException("maintenance_outside_source");
                var file = new FileInfo(path);
                var size = file.Length; var modified = file.LastWriteTimeUtc;
                if (index != request.AuditIndex && item.Sha256 is { Length: 64 } && item.FileSize == size && item.ModifiedUtc == modified)
                { results.Add(new(item.Id, item.IsVideo, "unchanged")); continue; }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, deadline.Token).ConfigureAwait(false)).ToLowerInvariant();
                var sameContent = hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase);
                var metadata = sameContent ? null : await _metadata(path, item.IsVideo, deadline.Token).ConfigureAwait(false);
                file.Refresh();
                MediaSourceIdentity.ResolveAllowed(path, roots);
                if (file.Length != size || file.LastWriteTimeUtc != modified) throw new IOException("maintenance_source_changed");
                operation.Token.ThrowIfCancellationRequested();
                results.Add(new(item.Id, item.IsVideo, "verified", new(path, size, modified, item.IsVideo, hash, metadata, Reused: sameContent)));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(results, Deferred: true); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var missing = false;
                if (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    try { missing = ConfirmMissing(item, roots, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(results, Deferred: true); }
                    catch (Exception checkException) when (checkException is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
                results.Add(new(item.Id, item.IsVideo, missing ? "missing" : "unknown", Error: missing ? null : exception.GetType().Name));
            }
        }
        return new(results);
    }

    private static bool ConfirmMissing(MediaConsistencyCandidate item, IReadOnlyList<string> roots, CancellationToken token)
    {
        var path = Path.GetFullPath(item.Path);
        var root = Path.GetFullPath(item.Root);
        if (!DirectoryMediaImport.Contains(root, path) || !roots.Any(allowed => DirectoryMediaImport.Contains(allowed, path))) return false;
        // An offline/inaccessible root is unknown, never evidence that every indexed file disappeared.
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) return false;
        string? readableParent = null;
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(parent);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                if ((attributes & FileAttributes.Directory) != 0) readableParent ??= parent;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
        }
        if (readableParent is null) return false;
        token.ThrowIfCancellationRequested();
        using (var entries = Directory.EnumerateFileSystemEntries(readableParent).GetEnumerator()) _ = entries.MoveNext();
        try { _ = File.GetAttributes(path); return false; }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return true; }
    }
}
