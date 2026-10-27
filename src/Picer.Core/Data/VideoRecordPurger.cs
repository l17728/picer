using System.Security.Cryptography;
using System.Text;

namespace Picer.Core.Data;

public enum VideoPurgeCacheScope { Local, Remote }

/// <summary>Explicit index/cache deletion only. Original media is never removed.</summary>
public sealed class VideoRecordPurger(PicerDatabase db, VideoPurgeCacheScope cacheScope = VideoPurgeCacheScope.Local, bool allowSourceCacheCleanup = true)
{
    public sealed record SourceCacheRequest(string Path, IReadOnlyList<string> Keyframes, IReadOnlyList<string>? ProtectedKeyframes = null);
    public sealed record Snapshot(long Id, long ContentId, string Path, bool LastReference, IReadOnlyList<string> Keyframes, IReadOnlyList<long> FaceIds)
    {
        public bool LastOwnerReference { get; init; }
    }

    public Snapshot? Inspect(long id)
    {
        using var record = db.Command("SELECT content_id,path,(SELECT COUNT(*) FROM videos copies WHERE copies.content_id=v.content_id) FROM videos v WHERE id=$id");
        record.Parameters.AddWithValue("$id", id);
        long content; string path; bool last;
        using (var reader = record.ExecuteReader())
        {
            if (!reader.Read()) return null;
            content = reader.GetInt64(0); path = reader.GetString(1); last = reader.GetInt64(2) == 1;
        }
        var frames = new List<string>(); var faces = new List<long>();
        using (var command = db.Command("SELECT path FROM video_keyframes WHERE video_id=$id"))
        {
            command.Parameters.AddWithValue("$id", content);
            using (var reader = command.ExecuteReader()) while (reader.Read()) frames.Add(reader.GetString(0));
        }
        using var ownerCount=db.Command("SELECT COUNT(*) FROM sync_video_owners WHERE content_id=$content AND owner_key=(SELECT owner_key FROM sync_video_owners WHERE video_id=$reference)");
        ownerCount.Parameters.AddWithValue("$content",content);ownerCount.Parameters.AddWithValue("$reference",id);
        var lastOwner=last || Convert.ToInt64(ownerCount.ExecuteScalar())==1;
        if (lastOwner)
        {
            using var faceCommand = db.Command("SELECT id FROM video_detected_faces WHERE video_id=$id AND ($last=1 OR owner_key=(SELECT owner_key FROM sync_video_owners WHERE video_id=$reference))");
            faceCommand.Parameters.AddWithValue("$id", content);faceCommand.Parameters.AddWithValue("$last",last);faceCommand.Parameters.AddWithValue("$reference",id);
            using var faceReader = faceCommand.ExecuteReader(); while (faceReader.Read()) faces.Add(faceReader.GetInt64(0));
        }
        return new(id, content, path, last, frames, faces) { LastOwnerReference=lastOwner };
    }

    public bool Purge(long id, bool requireMissing = false, bool transactionHeld = false)
    {
        using var transaction = transactionHeld ? null : db.BeginTransaction();
        var snapshot = Inspect(id);
        if (snapshot is null) return true;
        if (requireMissing && File.Exists(snapshot.Path)) return false;
        var root = Path.GetDirectoryName(db.DatabasePath)!;
        Picer.Core.Gateway.PlayableMediaCache.Purge(root);
        // Remove caches before committing so a failed cleanup retains the retry keys.
        var remote = cacheScope == VideoPurgeCacheScope.Remote;
        var frameRoot = Path.Combine(root, remote ? "remote-video-keyframes" : "video-keyframes");
        DeleteFiles(Path.Combine(root, remote ? "remote-video-summary-cache" : "video-summary-cache"), $"video-{id}-*");
        if (remote)
        {
            DeleteFiles(Path.Combine(root, "remote-assets"), $"video-{id}.*");
            DeleteFiles(Path.Combine(root, "remote-assets", "videos"), $"video-{id}-*");
        }
        var protectedFrames = GetProtectedKeyframes(snapshot).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        DeleteUnreferencedDirectory(frameRoot, id.ToString(System.Globalization.CultureInfo.InvariantCulture), protectedFrames);
        foreach (var staging in SafeDirectories(frameRoot, $".{id}-keyframes-*"))
            DeleteUnreferencedDirectory(frameRoot, Path.GetFileName(staging), protectedFrames);
        if (snapshot.LastReference)
        {
            foreach (var frame in snapshot.Keyframes) DeleteRecordedFrame(frameRoot, frame, protectedFrames, remote);
        }
        foreach (var face in snapshot.FaceIds)
            DeleteFiles(Path.Combine(root, remote ? "remote-video-face-thumbs" : "video-face-thumbs"), $"{face}.v*.jpg");
        if (allowSourceCacheCleanup && !remote && (OperatingSystem.IsWindows() || !snapshot.Path.Contains('\\')))
            PurgeSourceCaches(new(snapshot.Path, snapshot.Keyframes, protectedFrames.ToArray()));
        SpaceIdentityPurge.Video(db, id, snapshot.ContentId, snapshot.LastReference);
        db.PurgeVideoFaceRecords(snapshot.FaceIds);
        db.ReassignVideoMetadataEdits(id);
        using var remove = db.Command("""
            DELETE FROM metadata_edits WHERE entity_id=$id AND kind IN ('video-tag-link','video-concept');
            DELETE FROM mirror_owned_media WHERE kind='video' AND media_id=$id;
            DELETE FROM videos WHERE id=$id;
            """);
        remove.Parameters.AddWithValue("$id", id); remove.ExecuteNonQuery();
        Picer.Core.Indexing.DirectoryMediaChangeTracker.Shared.ForgetFile(snapshot.Path);
        Picer.Core.Video.LocalVideoContentHashCache.ForgetFile(snapshot.Path);
        transaction?.Commit(); return true;
    }

    public IReadOnlyList<string> GetProtectedKeyframes(Snapshot snapshot)
    {
        var protectedFrames = new List<string>();
        var root = Path.GetDirectoryName(db.DatabasePath)!;
        var candidates = new List<string>
        {
            Path.Combine(root, cacheScope == VideoPurgeCacheScope.Remote ? "remote-video-keyframes" : "video-keyframes").Replace('\\', '/') + "/",
            "/.picer-keyframes/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Picer.Core.Indexing.MediaSourcePath.ForStorage(snapshot.Path)))).ToLowerInvariant() + "/"
        };
        foreach (var frame in snapshot.Keyframes)
        {
            var normalized = frame.Replace('\\', '/'); var marker = normalized.IndexOf("/.picer-keyframes/", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                var end = normalized.IndexOf('/', marker + "/.picer-keyframes/".Length);
                if (end >= 0) candidates.Add(normalized[marker..(end + 1)]);
            }
        }
        using (var references = db.Command("SELECT path FROM video_keyframes WHERE $last=0 OR video_id<>$content"))
        {
            references.Parameters.AddWithValue("$last", snapshot.LastReference ? 1 : 0);
            references.Parameters.AddWithValue("$content", snapshot.ContentId);
            using var reader = references.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0); var normalized = path.Replace('\\', '/');
                if (candidates.Any(candidate => normalized.Contains(candidate, StringComparison.OrdinalIgnoreCase))) protectedFrames.Add(path);
            }
        }
        return protectedFrames;
    }

    public static string? RecordedSourceCacheDirectory(string frame)
    {
        if (string.IsNullOrWhiteSpace(frame)) return null;
        for (var parent = Path.GetDirectoryName(Path.GetFullPath(frame)); parent is not null; parent = Path.GetDirectoryName(parent))
            if (string.Equals(Path.GetFileName(Path.GetDirectoryName(parent)), ".picer-keyframes", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(parent) is { Length: 64 } hash && hash.All(Uri.IsHexDigit)) return parent;
        return null;
    }

    public static void PurgeSourceCaches(SourceCacheRequest request)
    {
        var protectedFrames = (request.ProtectedKeyframes ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var caches = request.Keyframes.Select(RecordedSourceCacheDirectory).Where(path => path is not null)
            .Cast<string>().Append(SourceCacheDirectory(request.Path)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in caches)
            DeleteUnreferencedDirectory(Path.GetDirectoryName(directory)!, Path.GetFileName(directory), protectedFrames);
    }

    public static string SourceCacheDirectory(string videoPath)
    {
        var full = Path.GetFullPath(videoPath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))).ToLowerInvariant();
        return Path.Combine(Path.GetDirectoryName(full)!, ".picer-keyframes", key);
    }

    public static void PurgeSourceCache(string videoPath)
    {
        if (!OperatingSystem.IsWindows() && (videoPath.Contains('\\') || (videoPath.Length > 1 && videoPath[1] == ':'))) return;
        var directory = SourceCacheDirectory(videoPath);
        DeleteDirectory(Path.GetDirectoryName(directory)!, Path.GetFileName(directory));
    }

    public static void PurgeRecordedSourceFrame(string frame)
    {
        if (string.IsNullOrWhiteSpace(frame)) return;
        var parent = Path.GetDirectoryName(Path.GetFullPath(frame));
        if (parent is not null && string.Equals(Path.GetFileName(Path.GetDirectoryName(parent)), ".picer-keyframes", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(parent) is { Length: 64 } hash && hash.All(Uri.IsHexDigit))
            DeleteDirectory(Path.GetDirectoryName(parent)!, hash);
    }

    private static void DeleteRecordedFrame(string frameRoot, string frame, HashSet<string> protectedFrames, bool remote)
    {
        if (string.IsNullOrWhiteSpace(frame)) return;
        if (!OperatingSystem.IsWindows() && (frame.Contains('\\') || (frame.Length > 1 && frame[1] == ':'))) return;
        var full = Path.GetFullPath(frame);
        var cache = Path.GetFullPath(frameRoot);
        if (full.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var referenceDirectory = Path.GetRelativePath(cache, full).Split(Path.DirectorySeparatorChar)[0];
            if (long.TryParse(referenceDirectory, out _)) DeleteUnreferencedDirectory(cache, referenceDirectory, protectedFrames);
            else if (!protectedFrames.Contains(full)) { EnsureNoLinks(full); File.Delete(full); }
            return;
        }
        if (remote) return;
        // Only the application's reserved source-side cache is eligible, never arbitrary stored paths.
        var sourceCache = RecordedSourceCacheDirectory(full);
        if (sourceCache is not null)
            DeleteUnreferencedDirectory(Path.GetDirectoryName(sourceCache)!, Path.GetFileName(sourceCache), protectedFrames);
    }

    private static void DeleteUnreferencedDirectory(string parent, string name, HashSet<string> protectedFrames)
    {
        var target = Path.GetFullPath(Path.Combine(parent, name));
        if (!Directory.Exists(target)) return;
        var prefix = target + Path.DirectorySeparatorChar;
        var referenced = protectedFrames.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (referenced.Length == 0) { DeleteDirectory(parent, name); return; }
        // Legacy frames can live directly beside the poster; keep that shared generation intact.
        if (referenced.Any(path => string.Equals(Path.GetDirectoryName(path), target, StringComparison.OrdinalIgnoreCase))) return;
        EnsureNoLinks(target);
        foreach (var child in Directory.EnumerateDirectories(target))
            DeleteUnreferencedDirectory(target, Path.GetFileName(child), protectedFrames);
    }

    private static void DeleteFiles(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return;
        EnsureNoLinks(directory);
        foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)) { EnsureNoLinks(file); File.Delete(file); }
    }

    private static IEnumerable<string> SafeDirectories(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return [];
        EnsureNoLinks(directory); return Directory.GetDirectories(directory, pattern, SearchOption.TopDirectoryOnly);
    }

    private static void DeleteDirectory(string parent, string name)
    {
        var root = Path.GetFullPath(parent); var target = Path.GetFullPath(Path.Combine(root, name));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("cache_path_outside_root");
        if (!Directory.Exists(target)) return;
        EnsureNoLinks(target);
        foreach (var item in Directory.EnumerateFileSystemEntries(target))
        {
            EnsureNoLinks(item);
            if (Directory.Exists(item)) DeleteDirectory(target, Path.GetFileName(item)); else File.Delete(item);
        }
        Directory.Delete(target);
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("purge_cache_link_rejected"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
