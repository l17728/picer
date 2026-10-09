using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.Core.Indexing;

public static class DirectoryMediaImport
{
    private sealed class ScanState(IReadOnlyList<PicerDatabase.DirectoryScanCandidate> candidates)
    {
        public IReadOnlyList<PicerDatabase.DirectoryScanCandidate> Candidates { get; } = candidates;
        public HashSet<string> Observed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Next;
        public bool Complete = true;
        public DateTime Created { get; } = DateTime.UtcNow;
    }
    private static readonly object ScanGate = new();
    private static readonly Dictionary<(string Db, long Source, string Directory, string Snapshot), ScanState> Scans = new();
    public static string Normalize(string path) => MediaSourcePath.Normalize(path);

    public static IReadOnlyList<DirectoryMediaEntry> KnownEntries(PicerDatabase db, long sourceId, string directory) =>
        db.GetImages(sourceId, includeMissing: false).Where(image => Contains(directory, image.Path))
            .Select(image => new DirectoryMediaEntry(image.Path, image.FileSize, image.ModifiedUtc, false, image.Sha256,
                new(image.Width, image.Height, image.TakenUtc, image.Camera, image.Gps,
                    AverageHash: image.AverageHash, DifferenceHash: image.DifferenceHash)))
            .Concat(new Picer.Core.Video.VideoRepository(db).GetAll().Where(video => video.Status == "active" && video.SourceId == sourceId && Contains(directory, video.Path))
                .Select(video => new DirectoryMediaEntry(video.Path, video.FileSize, video.ModifiedUtc, true, video.Sha256,
                    new(video.Width, video.Height, video.TakenUtc, Gps: video.Gps, DurationSeconds: video.DurationSeconds))))
            .ToArray();

    public static bool Contains(string root, string path) => !path.Replace('\\', '/').Split('/').Any(part => part is "." or "..")
        && (Normalize(path).Equals(Normalize(root), StringComparison.OrdinalIgnoreCase)
        || Normalize(path).StartsWith(Normalize(root) + "/", StringComparison.OrdinalIgnoreCase));

    public static void Apply(PicerDatabase db, long sourceId, string directory, DirectoryMediaPage page)
    {
        lock (ScanGate) ApplyCore(db, sourceId, directory, page);
    }

    public static bool ApplyAndDetectChanges(PicerDatabase db, long sourceId, string directory, DirectoryMediaPage page)
    {
        lock (ScanGate)
        {
            using var changes = db.Command("SELECT total_changes()");
            var before = Convert.ToInt64(changes.ExecuteScalar());
            ApplyCore(db, sourceId, directory, page);
            return Convert.ToInt64(changes.ExecuteScalar()) != before;
        }
    }

    private static void ApplyCore(PicerDatabase db, long sourceId, string directory, DirectoryMediaPage page)
    {
        var source = db.GetSource(sourceId) ?? throw new InvalidOperationException("directory_source_missing");
        if (!Contains(source.Path, directory)) throw new UnauthorizedAccessException("directory_outside_source");
        foreach (var entry in page.Items)
            if (!Contains(directory, entry.Path) || entry.FileSize < 0 ||
                (!entry.IsVideo && (entry.Sha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))))
                throw new InvalidDataException("invalid_directory_media_entry");
        var images = db.GetScanImagesByPaths(sourceId, page.Items.Where(x => !x.IsVideo).Select(x => x.Path))
            .ToDictionary(x => Normalize(x.Path), StringComparer.OrdinalIgnoreCase);
        var repository = new VideoRepository(db);
        var videos = db.GetScanVideosByPaths(sourceId, page.Items.Where(x => x.IsVideo).Select(x => x.Path))
            .ToDictionary(x => Normalize(x.Path), StringComparer.OrdinalIgnoreCase);
        var scanKey = (db.DatabasePath, sourceId, Normalize(directory), page.Snapshot);
        foreach (var expired in Scans.Where(x => x.Value.Created < DateTime.UtcNow.AddHours(-2)).Select(x => x.Key).ToArray()) Scans.Remove(expired);
        if (page.ObservedPaths is not null && page.StartOffset == 0 && !Scans.ContainsKey(scanKey))
        {
            if (Scans.Count >= 128) Scans.Remove(Scans.MinBy(x => x.Value.Created).Key);
            Scans[scanKey] = new(db.GetDirectoryScanCandidates(sourceId, directory));
        }
        Scans.TryGetValue(scanKey, out var scan);
        if (scan is not null && page.ObservedPaths is not null)
        {
            if (page.StartOffset != scan.Next || page.NextOffset != page.StartOffset + page.ObservedPaths.Count || page.ObservedPaths.Any(x => !Contains(directory, x))) scan.Complete = false;
            scan.Next = page.NextOffset;
            scan.Complete &= page.EnumerationComplete;
            scan.Observed.UnionWith(page.ObservedPaths.Select(Normalize));
        }
        using var transaction = db.BeginTransaction();
        foreach (var entry in page.Items)
        {
            if (entry.IsVideo)
            {
                videos.TryGetValue(Normalize(entry.Path), out var old);
                if (old is not null && old.FileSize == entry.FileSize && old.ModifiedUtc == entry.ModifiedUtc)
                {
                    RepairLegacySignature(db, "video_summaries", "video_id", new VideoContentRepository(db).GetContentId(old.Id), entry);
                    if (entry.Reused && old.Status == "active") continue;
                }
                var sameContent = entry.Sha256 is { Length: 64 } && string.Equals(entry.Sha256, old?.Sha256, StringComparison.OrdinalIgnoreCase);
                var changed = old is not null && !sameContent && (old.FileSize != entry.FileSize || old.ModifiedUtc != entry.ModifiedUtc || (entry.Sha256 is not null && old.Sha256 is not null && !string.Equals(entry.Sha256, old.Sha256, StringComparison.OrdinalIgnoreCase)));
                var metadata = entry.Metadata;
                var updated = old is null || changed
                    ? new VideoRecord(old?.Id ?? 0, sourceId, old?.Path ?? entry.Path, entry.FileSize, entry.ModifiedUtc, null, 0, 0, null, null, "active")
                    : old with { Status = "active", FileSize = entry.FileSize, ModifiedUtc = entry.ModifiedUtc };
                if (metadata is not null) updated = updated with { Width = metadata.Width, Height = metadata.Height,
                    DurationSeconds = metadata.DurationSeconds, TakenUtc = metadata.TakenUtc, Gps = metadata.Gps };
                updated = updated with { Sha256 = entry.Sha256 };
                repository.UpsertRemote(updated, sourceId);
                continue;
            }
            images.TryGetValue(Normalize(entry.Path), out var record);
            if (record is not null && record.Sha256 == entry.Sha256 && record.FileSize == entry.FileSize && record.ModifiedUtc == entry.ModifiedUtc && record.Status == ImageStatus.Active)
            {
                RepairLegacySignature(db, "cloud_vision_analyses", "image_id", record.Id, entry);
                if (entry.Metadata is null || entry.Reused) continue;
            }
            if (record is not null && record.Sha256 != entry.Sha256)
            {
                // Detach the physical projection first, retaining the old content-owned evidence.
                using var clear = db.Command("""
                    UPDATE images SET sha256=NULL,content_sha256=NULL WHERE id=$id;
                    DELETE FROM detected_faces WHERE image_id=$id;
                    DELETE FROM vision_analyses WHERE image_id=$id;
                    DELETE FROM cloud_vision_analyses WHERE image_id=$id;
                    DELETE FROM image_visual_concepts WHERE image_id=$id;
                    DELETE FROM florence_image_results WHERE image_id=$id;
                    DELETE FROM media_text_documents WHERE media_id=$id;
                    """);
                clear.Parameters.AddWithValue("$id", record.Id); clear.ExecuteNonQuery();
                record = new() { Id = record.Id, Path = record.Path };
            }
            record ??= new() { Path = entry.Path };
            if (entry.Metadata is { } photo)
            {
                record.Width = photo.Width; record.Height = photo.Height; record.TakenUtc = photo.TakenUtc;
                record.Camera = photo.Camera; record.Gps = photo.Gps;
                record.AverageHash = photo.AverageHash; record.DifferenceHash = photo.DifferenceHash;
            }
            record.SourceId = sourceId; record.Sha256 = entry.Sha256; record.FileSize = entry.FileSize;
            record.ModifiedUtc = entry.ModifiedUtc; record.Status = ImageStatus.Active; record.IndexedUtc = DateTime.UtcNow;
            db.UpsertContentInTransaction(entry.Sha256!, entry.FileSize, record.AverageHash, record.DifferenceHash, transaction);
            db.UpsertImageInTransaction(record, transaction);
        }
        if (page.Done && page.NextOffset == page.Total && scan is { Complete: true })
        {
            foreach (var missing in scan.Candidates.Where(x => !scan.Observed.Contains(Normalize(x.Path))))
            {
                using var command = db.Command($"UPDATE {(missing.Video ? "videos" : "images")} SET status='missing' WHERE id=$id AND source_id=$source AND path=$path AND file_size=$size AND modified_utc=$modified AND status<>'missing'");
                command.Parameters.AddWithValue("$id", missing.Id); command.Parameters.AddWithValue("$source", sourceId);
                command.Parameters.AddWithValue("$path", missing.Path); command.Parameters.AddWithValue("$size", missing.Size);
                command.Parameters.AddWithValue("$modified", missing.Modified.ToUniversalTime().ToString("o")); command.ExecuteNonQuery();
            }
        }
        transaction.Commit();
        if (page.Done) Scans.Remove(scanKey);
    }

    private static void RepairLegacySignature(PicerDatabase db, string table, string key, long id, DirectoryMediaEntry entry)
    {
        // Old synchronization dropped these fields. Repair only after a source scan
        // agrees with the indexed identity (including SHA-256 for photographs).
        using var command = db.Command($"UPDATE {table} SET source_file_size=$size,source_modified_utc=$modified WHERE {key}=$id AND (source_file_size=0 OR source_modified_utc='')");
        command.Parameters.AddWithValue("$size", entry.FileSize);
        command.Parameters.AddWithValue("$modified", entry.ModifiedUtc.ToUniversalTime().ToString("o"));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
