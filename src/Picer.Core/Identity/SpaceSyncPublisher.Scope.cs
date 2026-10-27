using Picer.Core.Data;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    /// <summary>Filters only the disposable, consistent snapshot. The user's local index and original files are untouched.</summary>
    private static void FilterSnapshot(PicerDatabase snapshot, SpaceUploadScope scope)
    {
        using var transaction = snapshot.BeginTransaction();
        // Filtering a disposable export is not a user deletion.
        using (var suppress=snapshot.Command("INSERT OR IGNORE INTO sync_suppression(id) VALUES(1)")) suppress.ExecuteNonQuery();
        using (var selected = snapshot.Command("CREATE TEMP TABLE selected_upload_sources(id INTEGER PRIMARY KEY)")) selected.ExecuteNonQuery();
        using (var add = snapshot.Command("INSERT OR IGNORE INTO selected_upload_sources VALUES($id)"))
            foreach (var source in scope.SourceIds) { add.Parameters.Clear(); add.Parameters.AddWithValue("$id", source); add.ExecuteNonQuery(); }
        var allSources = snapshot.GetSources().All(source => scope.SourceIds.Contains(source.Id));
        using(var block=snapshot.Command("""
            CREATE TABLE sync_export_blocked_video_deletions(content_id INTEGER PRIMARY KEY);
            INSERT OR IGNORE INTO sync_export_blocked_video_deletions
            SELECT content_id FROM videos WHERE source_id NOT IN(SELECT id FROM selected_upload_sources);
            """)) block.ExecuteNonQuery();
        // Unconfirmed imports are retained in the source database, but cannot be
        // published as newly created objects when their original scope is paused.
        using (var pending = snapshot.Command("""
            DELETE FROM tags WHERE id IN (SELECT local_id FROM sync_pending_identity_adoptions WHERE kind='tag');
            DELETE FROM albums WHERE id IN (SELECT local_id FROM sync_pending_identity_adoptions WHERE kind='album');
            DELETE FROM people WHERE id IN (SELECT local_id FROM sync_pending_identity_adoptions WHERE kind='person');
            DELETE FROM detected_faces WHERE id IN (SELECT local_id FROM sync_pending_identity_adoptions WHERE kind='face');
            DELETE FROM video_keyframes WHERE id IN (SELECT local_id FROM sync_pending_identity_adoptions WHERE kind='keyframe');
            DELETE FROM video_detected_faces WHERE id IN (SELECT local_id FROM sync_pending_identity_adoptions WHERE kind='video-face');
            """)) pending.ExecuteNonQuery();
        if (!allSources || !scope.Videos)
        {
            // A content decision may currently use an excluded reference as its
            // canonical local ID. Hand it to a surviving reference of the same
            // owner before removing references from this disposable snapshot.
            while (true)
            {
                var removed=new List<long>();
                using (var query=snapshot.Command("SELECT id FROM videos WHERE $videos=0 OR source_id NOT IN (SELECT id FROM selected_upload_sources) ORDER BY id LIMIT 128"))
                {
                    query.Parameters.AddWithValue("$videos",scope.Videos?1:0);
                    using var rows=query.ExecuteReader(); while (rows.Read()) removed.Add(rows.GetInt64(0));
                }
                if (removed.Count==0) break;
                foreach (var id in removed)
                {
                    snapshot.ReassignVideoMetadataEdits(id);
                    using var remove=snapshot.Command("DELETE FROM videos WHERE id=$id"); remove.Parameters.AddWithValue("$id",id); remove.ExecuteNonQuery();
                }
            }
        }
        using (var exclude = snapshot.Command("DELETE FROM sources WHERE id NOT IN (SELECT id FROM selected_upload_sources)")) exclude.ExecuteNonQuery();
        if (!scope.Photos) { using var remove = snapshot.Command("DELETE FROM images"); remove.ExecuteNonQuery(); }
        if (!scope.Videos) { using var remove = snapshot.Command("DELETE FROM videos"); remove.ExecuteNonQuery(); }
        if (!scope.People)
        {
            using var journal=snapshot.Command("DELETE FROM sync_metadata_row_journal WHERE table_name IN ('detected_faces','video_detected_faces','face_person_assignments','face_person_rejections','face_review_states','face_corrections','face_candidate_members')");journal.ExecuteNonQuery();
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
            using var journal=snapshot.Command("DELETE FROM sync_metadata_row_journal WHERE table_name='image_locations'");journal.ExecuteNonQuery();
            using var remove = snapshot.Command("UPDATE images SET gps=NULL; UPDATE videos SET gps=NULL; DELETE FROM image_locations;");
            remove.ExecuteNonQuery();
        }
        if (!allSources || !scope.Photos || !scope.Videos)
        {
            using var prune = snapshot.Command("""
                DELETE FROM people WHERE id NOT IN (SELECT person_id FROM face_person_assignments)
                    AND id NOT IN (SELECT person_id FROM face_person_rejections)
                    AND id NOT IN (SELECT CAST(field AS INTEGER) FROM metadata_edits WHERE kind='face-rejection')
                    AND id NOT IN (SELECT person_id FROM video_face_person_rejections)
                    AND id NOT IN (SELECT CAST(field AS INTEGER) FROM metadata_edits WHERE kind='video-face-rejection')
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
                (kind='scene-description' AND NOT EXISTS(SELECT 1 FROM scene_group_descriptions WHERE fingerprint=field)) OR
                (kind='person' AND NOT EXISTS(SELECT 1 FROM people WHERE id=entity_id)) OR
                (kind IN ('face-state','face-rejection') AND NOT EXISTS(SELECT 1 FROM detected_faces WHERE id=entity_id)) OR
                (kind='face-rejection' AND NOT EXISTS(SELECT 1 FROM people WHERE id=CAST(field AS INTEGER))) OR
                (kind IN ('video-face-state','video-face-rejection') AND NOT EXISTS(SELECT 1 FROM video_effective_faces WHERE id=entity_id)) OR
                (kind='video-face-rejection' AND NOT EXISTS(SELECT 1 FROM people WHERE id=CAST(field AS INTEGER))) OR
                (kind='tag-link' AND (NOT EXISTS(SELECT 1 FROM images WHERE id=entity_id) OR NOT EXISTS(SELECT 1 FROM tags WHERE id=CAST(field AS INTEGER)))) OR
                (kind='album-link' AND (NOT EXISTS(SELECT 1 FROM albums WHERE id=entity_id) OR NOT EXISTS(SELECT 1 FROM images WHERE id=CAST(field AS INTEGER)))) OR
                (kind IN ('video-tag-link','video-concept') AND NOT EXISTS(SELECT 1 FROM videos WHERE id=entity_id));
            """)) edits.ExecuteNonQuery();
        using (var suppress=snapshot.Command("DELETE FROM sync_suppression WHERE id=1")) suppress.ExecuteNonQuery();
        transaction.Commit();
    }
}
