using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    internal void PurgeVideoFaceRecords(IReadOnlyList<long> faceIds)
    {
        if(faceIds.Count==0) return;
        using var command=Command("""
            DELETE FROM sync_entity_identities WHERE kind='video-face' AND
                (target_id IN (SELECT value FROM json_each($ids)) OR (target_id IS NULL AND CAST(local_key AS INTEGER) IN (SELECT value FROM json_each($ids))));
            DELETE FROM sync_identity_offers WHERE kind='video-face' AND target_id IN (SELECT value FROM json_each($ids));
            DELETE FROM sync_pending_identity_adoptions WHERE kind='video-face' AND local_id IN (SELECT value FROM json_each($ids));
            DELETE FROM video_content_decision_history WHERE original_face_id IN (SELECT value FROM json_each($ids));
            DELETE FROM video_detected_faces WHERE id IN (SELECT value FROM json_each($ids));
            DELETE FROM sync_metadata_row_journal WHERE
                (table_name='video_detected_faces' AND COALESCE(json_extract(after_payload,'$.id'),json_extract(before_payload,'$.id')) IN (SELECT value FROM json_each($ids))) OR
                (table_name='video_face_person_rejections' AND COALESCE(json_extract(after_payload,'$.video_face_id'),json_extract(before_payload,'$.video_face_id')) IN (SELECT value FROM json_each($ids)));
            """);
        command.Parameters.AddWithValue("$ids",System.Text.Json.JsonSerializer.Serialize(faceIds));command.ExecuteNonQuery();
    }

    public void RequireOwnedVideoFace(long faceId,SqliteTransaction? transaction=null)
    {
        using var query=Command("SELECT 1 FROM video_effective_faces WHERE id=$id LIMIT 1");
        if(transaction is not null) query.Transaction=transaction;
        query.Parameters.AddWithValue("$id",faceId);
        if(query.ExecuteScalar() is null) throw new InvalidOperationException("video_face_ownership_review_required");
    }

    private void RequireVideoFaceOwner(long faceId,string owner,SqliteTransaction transaction)
    {
        using var query=Command("SELECT owner_key FROM video_detected_faces WHERE id=$id");query.Transaction=transaction;
        query.Parameters.AddWithValue("$id",faceId);
        if(query.ExecuteScalar() is string existing && existing!=owner) throw new InvalidDataException("video_face_owner_changed");
    }

    public string GetVideoOwnerKey(long referenceId,SqliteTransaction? transaction=null)
    {
        using var query=Command("SELECT owner_key FROM sync_video_owners WHERE video_id=$id");
        if(transaction is not null) query.Transaction=transaction;
        query.Parameters.AddWithValue("$id",referenceId);
        var owner=query.ExecuteScalar() as string??throw new InvalidOperationException("video_reference_missing");
        if(owner.StartsWith("ambiguous-source:",StringComparison.Ordinal)) throw new InvalidOperationException("video_source_ownership_ambiguous");
        return owner;
    }
}
