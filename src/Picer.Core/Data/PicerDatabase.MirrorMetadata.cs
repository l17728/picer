using System.Text.Json;
using Picer.Core.Gateway;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    /// <summary>Remove relationships absent from an authoritative snapshot before its upserts.
    /// Only references owned by the mirror are eligible; local pending edits are replayed afterwards.</summary>
    public void ReconcileMirroredPhotoMetadata(GatewayMetadata metadata, bool authoritativeCatalog = false)
    {
        using var transaction = BeginTransaction();
        using var staging = Command("CREATE TEMP TABLE IF NOT EXISTS mirror_expected_keys(value TEXT PRIMARY KEY); DELETE FROM mirror_expected_keys;");
        staging.ExecuteNonQuery();
        const string images = "SELECT media_id FROM mirror_owned_media WHERE kind='image'";
        Keep("image_tags", "printf('%d:%d',image_id,tag_id)", metadata.ImageTags.Select(x => $"{x.ImageId}:{x.TagId}"), $"image_id IN ({images})");
        Keep("album_images", "printf('%d:%d',image_id,album_id)", metadata.AlbumImages.Select(x => $"{x.ImageId}:{x.AlbumId}"), $"image_id IN ({images})");
        Keep("image_locations", "CAST(image_id AS TEXT)", metadata.Locations.Select(x => x.ImageId.ToString(System.Globalization.CultureInfo.InvariantCulture)), $"image_id IN ({images})");
        Keep("face_person_assignments", "CAST(face_id AS TEXT)", metadata.FaceAssignments.Select(x => x.FaceId.ToString(System.Globalization.CultureInfo.InvariantCulture)), $"face_id IN (SELECT id FROM detected_faces WHERE image_id IN ({images}))");
        Keep("detected_faces", "CAST(id AS TEXT)", metadata.Faces.Select(x => x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), $"image_id IN ({images})");
        if (metadata.CloudVisionAnalyses is not null)
            Keep("cloud_vision_analyses", "printf('%d:%s',image_id,provider)", metadata.CloudVisionAnalyses.Select(x => $"{x.ImageId}:{x.Provider}"), $"image_id IN ({images})");
        // Null means an older gateway did not supply this collection, not an empty snapshot.
        if (authoritativeCatalog && metadata.SceneGroupDescriptions is not null)
            Keep("scene_group_descriptions", "fingerprint", metadata.SceneGroupDescriptions.Select(x => x.Fingerprint), "1=1");
        staging.ExecuteNonQuery();
        transaction.Commit();

        void Keep(string table, string key, IEnumerable<string> values, string scope)
        {
            using var expected = Command("DELETE FROM mirror_expected_keys; INSERT OR IGNORE INTO mirror_expected_keys SELECT value FROM json_each($keys)");
            expected.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(values));
            expected.ExecuteNonQuery();
            // Identifiers/expressions are fixed above; external values are always parameters.
            using var remove = Command($"DELETE FROM {table} WHERE ({scope}) AND ({key}) NOT IN (SELECT value FROM mirror_expected_keys)");
            remove.ExecuteNonQuery();
        }
    }

    /// <summary>Only a dedicated service mirror may reconcile whole catalogs. Pending local registers
    /// retain their targets; acknowledged historical registers must not resurrect remote deletions.</summary>
    public void ReconcileMirroredCatalog(string kind, IEnumerable<long> remoteIds, bool authoritative,
        IReadOnlySet<string>? acknowledgedOperations = null)
    {
        if (!authoritative) return;
        var (table, editMatch) = kind switch
        {
            "tag" => ("tags", "e.kind IN ('tag-link','video-tag-link') AND e.field=CAST(t.id AS TEXT)"),
            "album" => ("albums", "e.kind='album-link' AND e.entity_id=t.id"),
            "person" => ("people", "e.kind='person' AND e.entity_id=t.id"),
            _ => throw new ArgumentException("invalid_mirror_catalog", nameof(kind))
        };
        using var transaction = BeginTransaction();
        using (var staging = Command("""
            CREATE TEMP TABLE IF NOT EXISTS mirror_catalog_remote(id INTEGER PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS mirror_catalog_ack(operation_id TEXT PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS mirror_catalog_remove(id INTEGER PRIMARY KEY);
            DELETE FROM mirror_catalog_remote; DELETE FROM mirror_catalog_ack; DELETE FROM mirror_catalog_remove;
            INSERT OR IGNORE INTO mirror_catalog_remote SELECT value FROM json_each($ids);
            INSERT OR IGNORE INTO mirror_catalog_ack SELECT value FROM json_each($acks);
            """))
        {
            staging.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(remoteIds));
            staging.Parameters.AddWithValue("$acks", JsonSerializer.Serialize(acknowledgedOperations?.ToArray() ?? []));
            staging.ExecuteNonQuery();
        }
        using (var candidates = Command($"""
            INSERT INTO mirror_catalog_remove
            SELECT t.id FROM {table} t WHERE t.id NOT IN (SELECT id FROM mirror_catalog_remote)
            AND NOT EXISTS(SELECT 1 FROM metadata_edits e WHERE ({editMatch})
                AND e.operation_id NOT IN (SELECT operation_id FROM mirror_catalog_ack));
            """)) candidates.ExecuteNonQuery();
        var editDelete = kind switch
        {
            "tag" => "kind IN ('tag-link','video-tag-link') AND CAST(field AS INTEGER) IN (SELECT id FROM mirror_catalog_remove)",
            "album" => "kind='album-link' AND entity_id IN (SELECT id FROM mirror_catalog_remove)",
            _ => "kind='person' AND entity_id IN (SELECT id FROM mirror_catalog_remove)"
        };
        using (var remove = Command($"DELETE FROM metadata_edits WHERE {editDelete}; DELETE FROM {table} WHERE id IN (SELECT id FROM mirror_catalog_remove);"))
            remove.ExecuteNonQuery();
        transaction.Commit();
    }
}
