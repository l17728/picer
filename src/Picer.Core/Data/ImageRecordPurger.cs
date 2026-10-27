using System.Security.Cryptography;
using System.Text;
using Picer.Core.Models;

namespace Picer.Core.Data;

/// <summary>Explicit user-requested purge; missing-file scans never call this.</summary>
public sealed class ImageRecordPurger(PicerDatabase db)
{
    public bool Purge(long id, bool requireMissing = true, bool transactionHeld = false)
    {
        using var transaction = transactionHeld ? null : db.BeginTransaction();
        var record = db.GetImage(id);
        if (record is null) return true;
        if (requireMissing && record.Status != ImageStatus.Missing) return false;
        Picer.Core.Gateway.PlayableMediaCache.Purge(Path.GetDirectoryName(db.DatabasePath)!);
        var hash = record.ContentSha256 ?? record.Sha256;
        // Explicit index purge forgets the last owned human decision cache.
        // Internal physical-reference deletion may retain it for content recovery.
        using(var ownedCache=db.Command("""
            DELETE FROM content_derivatives
            WHERE content_sha256=$hash
              AND kind IN ('face_person_assignments','face_person_rejections','face_review_states','face_corrections')
              AND json_extract(item_key,'$[#-1]')=(SELECT owner_key FROM sync_image_owners WHERE image_id=$id)
              AND NOT EXISTS(SELECT 1 FROM images i JOIN sync_image_owners o ON o.image_id=i.id
                  WHERE i.id<>$id AND COALESCE(i.content_sha256,i.sha256)=content_derivatives.content_sha256
                    AND o.owner_key=json_extract(content_derivatives.item_key,'$[#-1]'));
            """))
        {
            ownedCache.Parameters.AddWithValue("$hash",(object?)hash??DBNull.Value);
            ownedCache.Parameters.AddWithValue("$id",id);ownedCache.ExecuteNonQuery();
        }
        var faces = new List<long>();
        using (var command = db.Command("SELECT id FROM detected_faces WHERE image_id=$id"))
        {
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader(); while (reader.Read()) faces.Add(reader.GetInt64(0));
        }
        SpaceIdentityPurge.Photo(db, id);
        using (var command = db.Command("""
            DELETE FROM face_person_prototypes WHERE person_id IN (
                SELECT person_id FROM face_person_assignments WHERE face_id IN (SELECT id FROM detected_faces WHERE image_id=$id));
            DELETE FROM face_person_policies WHERE person_id IN (
                SELECT person_id FROM face_person_assignments WHERE face_id IN (SELECT id FROM detected_faces WHERE image_id=$id));
            DELETE FROM face_training_jobs WHERE artifact_id IN (
                SELECT artifact_id FROM face_training_artifacts WHERE dataset_id IN (
                    SELECT snapshot_id FROM face_dataset_members WHERE face_id IN (SELECT id FROM detected_faces WHERE image_id=$id)));
            DELETE FROM face_training_artifacts WHERE dataset_id IN (
                SELECT snapshot_id FROM face_dataset_members WHERE face_id IN (SELECT id FROM detected_faces WHERE image_id=$id));
            DELETE FROM face_dataset_snapshots WHERE snapshot_id IN (
                SELECT snapshot_id FROM face_dataset_members WHERE face_id IN (SELECT id FROM detected_faces WHERE image_id=$id));
            DELETE FROM face_training_snapshots WHERE id IN (SELECT snapshot_id FROM face_snapshot_members WHERE image_id=$id)
                OR id IN (SELECT snapshot_id FROM face_snapshot_rejections WHERE face_id IN (SELECT id FROM detected_faces WHERE image_id=$id));
            DELETE FROM face_action_log WHERE face_id IN (SELECT id FROM detected_faces WHERE image_id=$id)
                OR EXISTS(SELECT 1 FROM json_each(CASE WHEN json_valid(co_clustered_face_ids) THEN co_clustered_face_ids ELSE '[]' END) j
                    WHERE j.value IN (SELECT id FROM detected_faces WHERE image_id=$id));
            DELETE FROM face_user_action_events WHERE face_id IN (SELECT id FROM detected_faces WHERE image_id=$id);
            DELETE FROM metadata_edits WHERE (kind='tag-link' AND entity_id=$id) OR (kind='album-link' AND field=CAST($id AS TEXT));
            DELETE FROM scene_group_descriptions WHERE EXISTS(SELECT 1 FROM json_each(image_ids_json) WHERE value=$id)
                OR EXISTS(SELECT 1 FROM json_each(representative_image_ids_json) WHERE value=$id);
            DELETE FROM media_text_documents WHERE media_id=$id;
            DELETE FROM mirror_owned_media WHERE kind='image' AND media_id=$id;
            DELETE FROM images WHERE id=$id;
            """))
        { command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery(); }
        var shared = false;
        var sharedLegacyThumbnail = false;
        if (record.ThumbPath is not null)
        {
            using var thumbnails = db.Command("SELECT 1 FROM images WHERE thumb_path=$path LIMIT 1");
            thumbnails.Parameters.AddWithValue("$path", record.ThumbPath);
            sharedLegacyThumbnail = thumbnails.ExecuteScalar() is not null;
        }
        if (hash is not null)
        {
            using var remaining = db.Command("SELECT 1 FROM images WHERE COALESCE(content_sha256,sha256)=$hash LIMIT 1");
            remaining.Parameters.AddWithValue("$hash", hash); shared = remaining.ExecuteScalar() is not null;
            if (!shared)
            {
                using var purge = db.Command("DELETE FROM content_derivatives WHERE content_sha256=$hash; DELETE FROM content_text_documents WHERE content_sha256=$hash; DELETE FROM image_contents WHERE sha256=$hash;");
                purge.Parameters.AddWithValue("$hash", hash); purge.ExecuteNonQuery();
            }
        }
        // Cache removal is required for success. If it fails, keep the database
        // record so the user can retry instead of losing the cleanup keys.
        var root = Path.GetDirectoryName(db.DatabasePath)!;
        foreach (var directory in new[] { "thumbs", "remote-thumbs" })
        {
            var folder = Path.Combine(root, directory);
            if (!Directory.Exists(folder)) continue;
            if (!sharedLegacyThumbnail)
            {
                foreach (var pattern in new[] { $"{id}.v*.jpg", $"{id}.jpg" }) DeleteMatches(folder, pattern);
                // Legacy copies can refer to a thumbnail named after an older
                // reference ID. Only remove a recorded path inside this cache.
                if (record.ThumbPath is not null && string.Equals(Path.GetDirectoryName(Path.GetFullPath(record.ThumbPath)),
                        Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(record.ThumbPath);
                    if (name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && !name.Contains('*') && !name.Contains('?')) DeleteMatches(folder, name);
                }
            }
            if (!shared)
            {
                var identity = (hash ?? $"id-{record.Id}").ToLowerInvariant();
                var cacheKey = identity.Length == 64 && identity.All(Uri.IsHexDigit) ? identity
                    : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
                DeleteMatches(folder, "v1-" + cacheKey + ".jpg");
                if (hash is { Length: 64 } && hash.All(Uri.IsHexDigit)) DeleteMatches(folder, $"sha256-{hash.ToLowerInvariant()}.s*.v*.jpg");
            }
        }
        foreach (var directory in new[] { "face-thumbs", "remote-face-thumbs" })
            foreach (var face in faces) DeleteMatches(Path.Combine(root, directory), $"{face}.v*.jpg");
        var assets = Path.Combine(root, "remote-assets");
        DeleteMatches(assets, $"image-{id}.*");
        DeleteMatches(Path.Combine(assets, "images"), $"image-{id}-*");
        if (!shared && hash is { Length: 64 } && hash.All(Uri.IsHexDigit))
        {
            DeleteMatches(assets, hash.ToLowerInvariant());
            DeleteMatches(assets, hash.ToLowerInvariant() + ".part");
        }
        Picer.Core.Indexing.DirectoryMediaChangeTracker.Shared.ForgetFile(record.Path);
        transaction?.Commit();
        return true;
    }

    private static void DeleteMatches(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("cache_directory_is_link");
        foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)) File.Delete(path);
    }
}
