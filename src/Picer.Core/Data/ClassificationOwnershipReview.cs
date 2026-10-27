using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class ClassificationOwnershipReview
{
    internal static void Record(PicerDatabase database, SqliteTransaction transaction, long imageId, string conceptId, string decision)
    {
        using var command = database.Command("""
            INSERT INTO classification_rejection_ownership_reviews(content_sha256,concept_id,owner_key,decision,reviewed_utc)
            SELECT lower(COALESCE(i.content_sha256,i.sha256)),$concept,o.owner_key,$decision,$now
            FROM images i JOIN sync_image_owners o ON o.image_id=i.id
            WHERE i.id=$image AND EXISTS(SELECT 1 FROM image_classification_rejections r
                WHERE r.owner_key='' AND r.concept_id=$concept
                  AND lower(r.content_sha256)=lower(COALESCE(i.content_sha256,i.sha256)))
            ON CONFLICT(content_sha256,concept_id,owner_key) DO UPDATE SET decision=excluded.decision,reviewed_utc=excluded.reviewed_utc;
            """);
        command.Transaction = transaction; command.Parameters.AddWithValue("$image", imageId);
        command.Parameters.AddWithValue("$concept", conceptId); command.Parameters.AddWithValue("$decision", decision);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }
}
