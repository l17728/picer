using Picer.Core.Data;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    /// <summary>Filters only the disposable, consistent snapshot. The user's local index and original files are untouched.</summary>
    private static void FilterSnapshot(PicerDatabase snapshot, SpaceUploadScope scope)
    {
        using var transaction = snapshot.BeginTransaction();
        using (var selected = snapshot.Command("CREATE TEMP TABLE selected_upload_sources(id INTEGER PRIMARY KEY)")) selected.ExecuteNonQuery();
        using (var add = snapshot.Command("INSERT OR IGNORE INTO selected_upload_sources VALUES($id)"))
            foreach (var source in scope.SourceIds) { add.Parameters.Clear(); add.Parameters.AddWithValue("$id", source); add.ExecuteNonQuery(); }
        var allSources = snapshot.GetSources().All(source => scope.SourceIds.Contains(source.Id));
        using (var exclude = snapshot.Command("DELETE FROM sources WHERE id NOT IN (SELECT id FROM selected_upload_sources)")) exclude.ExecuteNonQuery();
        if (!scope.Photos) { using var remove = snapshot.Command("DELETE FROM images"); remove.ExecuteNonQuery(); }
        if (!scope.Videos) { using var remove = snapshot.Command("DELETE FROM videos"); remove.ExecuteNonQuery(); }
        if (!scope.People)
        {
            using var remove = snapshot.Command("DELETE FROM detected_faces; DELETE FROM video_detected_faces; DELETE FROM people; DELETE FROM face_candidate_clusters;");
            remove.ExecuteNonQuery();
        }
        if (!allSources || !scope.People || !scope.Photos || !scope.Videos)
        {
            // Trained aggregates can include evidence from excluded roots. Do not export them as if they belonged only to the selected roots.
            using var remove = snapshot.Command("DELETE FROM face_person_prototypes; DELETE FROM face_person_policies; DELETE FROM face_training_artifacts;");
            remove.ExecuteNonQuery();
        }
        if (!scope.PreciseLocation)
        {
            using var remove = snapshot.Command("UPDATE images SET gps=NULL; UPDATE videos SET gps=NULL; DELETE FROM image_locations;");
            remove.ExecuteNonQuery();
        }
        if (!allSources || !scope.Photos || !scope.Videos)
        {
            using var prune = snapshot.Command("""
                DELETE FROM people WHERE id NOT IN (SELECT person_id FROM face_person_assignments)
                    AND id NOT IN (SELECT assigned_person_id FROM video_detected_faces WHERE assigned_person_id IS NOT NULL)
                    AND id NOT IN (SELECT suggested_person_id FROM video_detected_faces WHERE suggested_person_id IS NOT NULL);
                DELETE FROM albums WHERE id NOT IN (SELECT album_id FROM album_images);
                DELETE FROM tags WHERE id NOT IN (SELECT tag_id FROM image_tags) AND id NOT IN (SELECT tag_id FROM video_tags);
                DELETE FROM scene_group_descriptions WHERE EXISTS(SELECT 1 FROM json_each(image_ids_json) j WHERE NOT EXISTS(SELECT 1 FROM images i WHERE i.id=j.value));
                """);
            prune.ExecuteNonQuery();
        }
        using (var edits = snapshot.Command("""
            DELETE FROM metadata_edits WHERE
                (kind='person' AND NOT EXISTS(SELECT 1 FROM people WHERE id=entity_id)) OR
                (kind='tag-link' AND (NOT EXISTS(SELECT 1 FROM images WHERE id=entity_id) OR NOT EXISTS(SELECT 1 FROM tags WHERE id=CAST(field AS INTEGER)))) OR
                (kind='album-link' AND (NOT EXISTS(SELECT 1 FROM albums WHERE id=entity_id) OR NOT EXISTS(SELECT 1 FROM images WHERE id=CAST(field AS INTEGER)))) OR
                (kind IN ('video-tag-link','video-concept') AND NOT EXISTS(SELECT 1 FROM videos WHERE id=entity_id));
            """)) edits.ExecuteNonQuery();
        transaction.Commit();
    }
}
