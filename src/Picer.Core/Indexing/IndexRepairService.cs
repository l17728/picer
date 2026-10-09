using System.Security.Cryptography;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.Core.Indexing;

public sealed record IndexRepairOperation(long ImageId, string OldPath, string? Sha256,
    long FileSize, DateTime ModifiedUtc, string? NewPath, string? Root, bool AddReference = false, bool IsVideo = false);
public sealed record IndexRepairEvidence(bool OldMissing, DirectoryMediaEntry? Target, string? Error = null);
public sealed record IndexRepairResult(long ImageId, string Status, string? Error = null);
public sealed record IndexRepairPlan(IReadOnlyList<IndexRepairOperation> Operations, IReadOnlyList<DirectoryMediaFailure> Failures);

/// <summary>Read-only discovery and verified rebinding of physical references to content.</summary>
public static class IndexRepairService
{
    public static string LocalPath(string path) => DirectoryMediaImport.Normalize(path).Replace('/', Path.DirectorySeparatorChar);

    public static async Task<IndexRepairPlan> BuildAsync(IReadOnlyList<ImageRecord> images, IReadOnlyList<string> roots,
        IProgress<string>? progress = null, CancellationToken token = default, IReadOnlyList<VideoRecord>? videos = null)
    {
        images = images.Concat((videos ?? []).Select(video => new ImageRecord { Id = -video.Id, SourceId = video.SourceId, Path = video.Path, Sha256 = video.Sha256, FileSize = video.FileSize, ModifiedUtc = video.ModifiedUtc, Status = video.Status == "active" ? ImageStatus.Active : ImageStatus.Missing })).ToArray();
        var selected = roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var failures = new List<DirectoryMediaFailure>();
        var missing = new List<ImageRecord>();
        foreach (var image in images)
        {
            token.ThrowIfCancellationRequested();
            try { if (IsMissing(LocalPath(image.Path))) missing.Add(image); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { failures.Add(new(image.Path, exception.GetType().Name)); }
        }
        // Repair needs identity, not decoding or AI. Corrupt-but-identical images
        // can still be restored; full hashes also identify video references.
        var scanner = new DirectoryMediaScanner((_, _, _) => Task.FromResult(new DirectoryMediaMetadata(0, 0)));
        var candidates = new Dictionary<string, DirectoryMediaEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in selected.Where(root => !selected.Any(other => !root.Equals(other, StringComparison.OrdinalIgnoreCase)
                     && DirectoryMediaImport.Contains(other, root))))
        {
            var request = new DirectoryMediaScanRequest(0, root, Limit: 25);
            try
            {
                while (true)
                {
                    var page = await scanner.ReadAsync(request, token).ConfigureAwait(false);
                    failures.AddRange(page.Failures ?? []);
                    foreach (var item in page.Items) candidates[DirectoryMediaImport.Normalize(item.Path)] = item;
                    progress?.Report($"扫描新位置：{root}，{page.NextOffset}/{page.Total}");
                    if (page.Done) break;
                    request = request with { Snapshot = page.Snapshot, Offset = page.NextOffset };
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            { failures.Add(new(root, exception.GetType().Name, true)); }
        }
        var occupied = images.Select(image => DirectoryMediaImport.Normalize(image.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingByHash = missing.Where(image => image.Sha256 is { Length: 64 }).ToLookup(image => (Hash: image.Sha256!.ToLowerInvariant(), Video: image.Id < 0));
        var candidatesByHash = candidates.Values.Where(item => item.Sha256 is not null && !occupied.Contains(DirectoryMediaImport.Normalize(item.Path)))
            .ToLookup(item => (Hash: item.Sha256!.ToLowerInvariant(), Video: item.IsVideo));
        var operations = new List<IndexRepairOperation>();
        var missingIds = missing.Select(image => image.Id).ToHashSet();
        foreach (var image in images.Where(image => image.Status == ImageStatus.Missing && !missingIds.Contains(image.Id)))
            if (candidates.TryGetValue(DirectoryMediaImport.Normalize(image.Path), out var present) && string.Equals(present.Sha256, image.Sha256, StringComparison.OrdinalIgnoreCase) && present.IsVideo == (image.Id < 0))
                operations.Add(Operation(image, present));
        foreach (var group in images.Where(image => image.Sha256 is { Length: 64 }).GroupBy(image => (Hash: image.Sha256!.ToLowerInvariant(), Video: image.Id < 0)))
        {
            var oldReferences = missingByHash[group.Key].OrderBy(image => image.Id).ToList();
            var newReferences = candidatesByHash[group.Key].OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();
            // Identical bytes are one content identity. Reuse old reference IDs
            // one-to-one, preferring the same filename; extra copies add references.
            foreach (var image in oldReferences)
            {
                var target = newReferences.FirstOrDefault(item => string.Equals(Path.GetFileName(item.Path), Path.GetFileName(LocalPath(image.Path)), StringComparison.OrdinalIgnoreCase))
                    ?? newReferences.FirstOrDefault();
                if (target is not null) newReferences.Remove(target);
                operations.Add(Operation(image, target));
            }
            var template = group.First();
            foreach (var target in newReferences) operations.Add(Operation(template, target) with { AddReference = true });
        }
        foreach (var image in missing.Where(image => image.Sha256 is not { Length: 64 })) operations.Add(Operation(image, null));
        // Add-copy operations must run before relocating their template reference.
        return new(operations.OrderByDescending(operation => operation.AddReference).ToArray(), failures.Distinct().ToArray());

        IndexRepairOperation Operation(ImageRecord image, DirectoryMediaEntry? target) => new(Math.Abs(image.Id), image.Path, image.Sha256,
            image.FileSize, image.ModifiedUtc, target?.Path, target is null ? null : selected.Where(root => DirectoryMediaImport.Contains(root, target.Path)).OrderByDescending(root => root.Length).First(), IsVideo: image.Id < 0);
    }

    public static async Task<IndexRepairEvidence> VerifyAsync(IndexRepairOperation operation, CancellationToken token = default)
    {
        try
        {
            var missing = IsMissing(LocalPath(operation.OldPath));
            var samePath = operation.NewPath is not null && DirectoryMediaImport.Normalize(operation.OldPath)
                .Equals(DirectoryMediaImport.Normalize(operation.NewPath), StringComparison.OrdinalIgnoreCase);
            if (!missing && !operation.AddReference && !samePath) return new(false, null);
            if (operation.NewPath is null) return new(missing, null);
            if (operation.Root is null || !DirectoryMediaImport.Contains(operation.Root, operation.NewPath)) return new(missing, null, "target_outside_selected_root");
            var path = LocalPath(operation.NewPath);
            EnsureNoLinks(path);
            var info = new FileInfo(path);
            var size = info.Length; var modified = info.LastWriteTimeUtc;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
            info.Refresh();
            if (info.Length != size || info.LastWriteTimeUtc != modified) return new(missing, null, "target_changed_during_verification");
            if (!string.Equals(hash, operation.Sha256, StringComparison.OrdinalIgnoreCase)) return new(missing, null, "hash_mismatch");
            return new(missing, new(path, size, modified, operation.IsVideo, hash));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new(false, null, exception.GetType().Name); }
    }

    public static IndexRepairResult Apply(PicerDatabase db, IndexRepairOperation operation, IndexRepairEvidence evidence)
    {
        if (evidence.Error is not null) return new(operation.ImageId, "failed", evidence.Error);
        if (operation.IsVideo) return ApplyVideo(db, operation, evidence);
        using var transaction = db.BeginTransaction();
        var image = db.GetImage(operation.ImageId);
        if (image is null || image.Path != operation.OldPath || image.Sha256 != operation.Sha256 || image.FileSize != operation.FileSize || image.ModifiedUtc != operation.ModifiedUtc)
            return new(operation.ImageId, "failed", "index_changed_since_scan");
        var samePath = operation.NewPath is not null && DirectoryMediaImport.Normalize(operation.OldPath)
            .Equals(DirectoryMediaImport.Normalize(operation.NewPath), StringComparison.OrdinalIgnoreCase);
        if (!evidence.OldMissing && !operation.AddReference && !samePath) return new(image.Id, "unchanged");
        if (evidence.Target is not { } target)
        {
            if (!operation.AddReference && evidence.OldMissing)
            {
                using var mark = db.Command("UPDATE images SET status='missing' WHERE id=$id");
                mark.Parameters.AddWithValue("$id", image.Id); mark.ExecuteNonQuery();
            }
            transaction.Commit();
            return new(image.Id, "missing");
        }
        if (target.IsVideo || target.Sha256 != image.Sha256 || operation.NewPath is null || operation.Root is null ||
            !DirectoryMediaImport.Normalize(target.Path).Equals(DirectoryMediaImport.Normalize(operation.NewPath), StringComparison.OrdinalIgnoreCase) ||
            !DirectoryMediaImport.Contains(operation.Root, target.Path)) return new(image.Id, "failed", "invalid_repair_evidence");
        using (var occupied = db.Command("SELECT 1 FROM images WHERE lower(replace(path,char(92),'/'))=lower(replace($path,char(92),'/')) AND id<>$self LIMIT 1"))
        {
            occupied.Parameters.AddWithValue("$path", target.Path);
            occupied.Parameters.AddWithValue("$self", operation.AddReference ? 0 : image.Id);
            if (occupied.ExecuteScalar() is not null) return new(image.Id, "failed", "target_already_indexed");
        }
        var oldModified = image.ModifiedUtc; var oldSize = image.FileSize;
        image.Path = target.Path; image.FileSize = target.FileSize; image.ModifiedUtc = target.ModifiedUtc;
        image.Status = ImageStatus.Active; image.IndexedUtc = DateTime.UtcNow;
        image.SourceId = db.AddSource(operation.Root).Id;
        if (operation.AddReference)
        {
            image.Id = 0;
            db.UpsertImageInTransaction(image, transaction);
            using var links = db.Command("""
                INSERT OR IGNORE INTO image_tags(image_id,tag_id) SELECT $new,tag_id FROM image_tags WHERE image_id=$old;
                INSERT OR IGNORE INTO album_images(album_id,image_id,added_utc) SELECT album_id,$new,added_utc FROM album_images WHERE image_id=$old;
                INSERT OR IGNORE INTO rejected_suggestions(image_id,tag_name,rejected_utc) SELECT $new,tag_name,rejected_utc FROM rejected_suggestions WHERE image_id=$old;
                INSERT OR IGNORE INTO image_locations(image_id,latitude,longitude,country,province,city,county,district,town,village,display_name,search_text,resolved_utc)
                    SELECT $new,latitude,longitude,country,province,city,county,district,town,village,display_name,search_text,resolved_utc FROM image_locations WHERE image_id=$old;
                """);
            links.Parameters.AddWithValue("$new", image.Id); links.Parameters.AddWithValue("$old", operation.ImageId); links.ExecuteNonQuery();
        }
        else
        {
            db.UpdateImageInTransaction(image, transaction);
            foreach (var table in new[] { "cloud_vision_analyses", "vision_analyses", "florence_image_results" })
            {
                using var rebind = db.Command($"UPDATE {table} SET source_file_size=$size,source_modified_utc=$modified WHERE image_id=$id AND source_file_size=$oldSize AND source_modified_utc=$oldModified");
                rebind.Parameters.AddWithValue("$size", target.FileSize); rebind.Parameters.AddWithValue("$modified", target.ModifiedUtc.ToString("o"));
                rebind.Parameters.AddWithValue("$oldSize", oldSize); rebind.Parameters.AddWithValue("$oldModified", oldModified.ToString("o"));
                rebind.Parameters.AddWithValue("$id", image.Id); rebind.ExecuteNonQuery();
            }
        }
        transaction.Commit();
        return new(image.Id, operation.AddReference ? "added" : "restored");
    }

    public static bool MatchesCurrent(PicerDatabase db, IndexRepairOperation operation)
    {
        if (operation.IsVideo)
        {
            var video = new VideoRepository(db).GetAll().FirstOrDefault(item => item.Id == operation.ImageId);
            return video is not null && video.Path == operation.OldPath && string.Equals(video.Sha256, operation.Sha256, StringComparison.OrdinalIgnoreCase) && video.FileSize == operation.FileSize && video.ModifiedUtc == operation.ModifiedUtc;
        }
        var image = db.GetImage(operation.ImageId);
        return image is not null && image.Path == operation.OldPath && image.Sha256 == operation.Sha256 && image.FileSize == operation.FileSize && image.ModifiedUtc == operation.ModifiedUtc;
    }

    private static IndexRepairResult ApplyVideo(PicerDatabase db, IndexRepairOperation operation, IndexRepairEvidence evidence)
    {
        using var transaction = db.BeginTransaction();
        if (!MatchesCurrent(db, operation)) return new(operation.ImageId, "failed", "index_changed_since_scan");
        var samePath = operation.NewPath is not null && DirectoryMediaImport.Normalize(operation.NewPath).Equals(DirectoryMediaImport.Normalize(operation.OldPath), StringComparison.OrdinalIgnoreCase);
        if (!evidence.OldMissing && !operation.AddReference && !samePath) return new(operation.ImageId, "unchanged");
        if (evidence.Target is not { } target)
        {
            if (evidence.OldMissing && !operation.AddReference)
            {
                using var missing = db.Command("UPDATE videos SET status='missing' WHERE id=$id");
                missing.Parameters.AddWithValue("$id", operation.ImageId); missing.ExecuteNonQuery();
            }
            transaction.Commit(); return new(operation.ImageId, "missing");
        }
        if (!target.IsVideo || operation.Sha256 is not { Length: 64 } || !string.Equals(target.Sha256, operation.Sha256, StringComparison.OrdinalIgnoreCase) || operation.NewPath is null || operation.Root is null ||
            !DirectoryMediaImport.Normalize(target.Path).Equals(DirectoryMediaImport.Normalize(operation.NewPath), StringComparison.OrdinalIgnoreCase) ||
            !DirectoryMediaImport.Contains(operation.Root, target.Path)) return new(operation.ImageId, "failed", "invalid_repair_evidence");
        using (var occupied = db.Command("SELECT 1 FROM videos WHERE lower(replace(path,char(92),'/'))=lower(replace($path,char(92),'/')) AND id<>$id LIMIT 1"))
        {
            occupied.Parameters.AddWithValue("$path", target.Path); occupied.Parameters.AddWithValue("$id", operation.AddReference ? 0 : operation.ImageId);
            if (occupied.ExecuteScalar() is not null) return new(operation.ImageId, "failed", "target_already_indexed");
        }
        var source = db.AddSource(operation.Root).Id;
        var resultId = operation.ImageId;
        if (operation.AddReference)
        {
            using var copy = db.Command("""
                INSERT INTO videos(source_id,path,file_size,modified_utc,duration_seconds,width,height,taken_utc,gps,status,indexed_utc,person_analysis_status,person_analyzed_utc,sha256)
                SELECT $source,$path,$size,$modified,duration_seconds,width,height,taken_utc,gps,'active',$now,person_analysis_status,person_analyzed_utc,lower(sha256) FROM videos WHERE id=$id RETURNING id
                """);
            Bind(copy); resultId = (long)copy.ExecuteScalar()!;
            // The insert trigger binds the verified hash to the existing content; no derivative copy.
        }
        else
        {
            // Identity has been verified by the source agent; do not call the normal upsert,
            // whose signature-change invalidation would discard this video's derived evidence.
            using var update = db.Command("UPDATE videos SET source_id=$source,path=$path,file_size=$size,modified_utc=$modified,status='active',indexed_utc=$now WHERE id=$id");
            Bind(update); update.ExecuteNonQuery();
        }
        using (var summary = db.Command("UPDATE video_summaries SET source_file_size=$size,source_modified_utc=$modified WHERE video_id=$id AND source_file_size=$oldSize AND source_modified_utc=$oldModified"))
        {
            summary.Parameters.AddWithValue("$id", new VideoContentRepository(db).GetContentId(resultId)); summary.Parameters.AddWithValue("$size", target.FileSize);
            summary.Parameters.AddWithValue("$modified", target.ModifiedUtc.ToUniversalTime().ToString("o")); summary.Parameters.AddWithValue("$oldSize", operation.FileSize);
            summary.Parameters.AddWithValue("$oldModified", operation.ModifiedUtc.ToUniversalTime().ToString("o")); summary.ExecuteNonQuery();
        }
        transaction.Commit(); return new(resultId, operation.AddReference ? "added" : "restored");

        void Bind(Microsoft.Data.Sqlite.SqliteCommand command)
        {
            command.Parameters.AddWithValue("$source", source); command.Parameters.AddWithValue("$path", target.Path);
            command.Parameters.AddWithValue("$size", target.FileSize); command.Parameters.AddWithValue("$modified", target.ModifiedUtc.ToUniversalTime().ToString("o"));
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o")); command.Parameters.AddWithValue("$id", operation.ImageId);
        }
    }

    private static bool IsMissing(string path)
    {
        try { EnsureNoLinks(path); return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }

    private static void EnsureNoLinks(string path)
    {
        // Check existing ancestors even when the leaf no longer exists.
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("repair_path_is_link");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
