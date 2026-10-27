namespace Picer.Core.Data;

/// <summary>Explicit media purge also forgets device-local identities and interrupted imports.</summary>
internal static class SpaceIdentityPurge
{
    internal static void Photo(PicerDatabase database, long imageId) => Remove(database,
        "(kind='image' AND local_key=CAST($id AS TEXT)) OR (kind='face' AND CAST(local_key AS INTEGER) IN (SELECT id FROM detected_faces WHERE image_id=$id))",
        "kind='face' AND local_id IN (SELECT id FROM detected_faces WHERE image_id=$id)", imageId, 0, false);

    internal static void Video(PicerDatabase database, long videoId, long contentId, bool lastReference) => Remove(database,
        "(kind='video' AND local_key=CAST($id AS TEXT)) OR ($last=1 AND ((kind='keyframe' AND CAST(local_key AS INTEGER) IN (SELECT id FROM video_keyframes WHERE video_id=$content)) OR (kind='video-face' AND CAST(local_key AS INTEGER) IN (SELECT id FROM video_detected_faces WHERE video_id=$content))))",
        "$last=1 AND ((kind='keyframe' AND local_id IN (SELECT id FROM video_keyframes WHERE video_id=$content)) OR (kind='video-face' AND local_id IN (SELECT id FROM video_detected_faces WHERE video_id=$content)))",
        videoId, contentId, lastReference);

    private static void Remove(PicerDatabase database, string identityPredicate, string pendingPredicate,
        long mediaId, long contentId, bool lastReference)
    {
        // The SQL fragments are fixed internal predicates. Receiving publishers use
        // different integer IDs and are cleaned by the service's authorized purge.
        using var command = database.Command($"""
            DELETE FROM sync_pending_identity_adoptions
            WHERE publisher_id IN (SELECT publisher_id FROM sync_publishers WHERE state IN ('publishing','rebaselining'))
                AND ({pendingPredicate});
            DELETE FROM sync_entity_identities
            WHERE publisher_id IN (SELECT publisher_id FROM sync_publishers WHERE state IN ('publishing','rebaselining'))
                AND ({identityPredicate});
            """);
        command.Parameters.AddWithValue("$id", mediaId); command.Parameters.AddWithValue("$content", contentId);
        command.Parameters.AddWithValue("$last", lastReference ? 1 : 0); command.ExecuteNonQuery();
    }
}
