namespace Picer.Core.Data;

public sealed class VideoContentRepository(PicerDatabase database)
{
    public long GetContentId(long videoId)
    {
        using var command = database.Command("SELECT content_id FROM videos WHERE id=$id");
        command.Parameters.AddWithValue("$id", videoId);
        return command.ExecuteScalar() is long id ? id : throw new InvalidOperationException("video_reference_missing");
    }

    public int GetReferenceCount(long contentId)
    {
        using var command = database.Command("SELECT COUNT(*) FROM videos WHERE content_id=$id");
        command.Parameters.AddWithValue("$id", contentId); return Convert.ToInt32(command.ExecuteScalar());
    }

    public void DetachChangedReference(long videoId)
    {
        var oldContent = GetContentId(videoId);
        database.ReassignVideoMetadataEdits(videoId);
        using var insert = database.Command("INSERT INTO video_contents(sha256,file_size) SELECT NULL,file_size FROM videos WHERE id=$id RETURNING id");
        insert.Parameters.AddWithValue("$id", videoId); var replacement = (long)insert.ExecuteScalar()!;
        using var update = database.Command("UPDATE videos SET content_id=$content,sha256=NULL,duration_seconds=NULL,width=0,height=0,taken_utc=NULL,gps=NULL,person_analysis_status=NULL,person_analyzed_utc=NULL WHERE id=$id");
        update.Parameters.AddWithValue("$content", replacement); update.Parameters.AddWithValue("$id", videoId); update.ExecuteNonQuery();
        using var cleanup = database.Command("DELETE FROM video_contents WHERE id=$old AND NOT EXISTS(SELECT 1 FROM videos WHERE content_id=$old)");
        cleanup.Parameters.AddWithValue("$old", oldContent); cleanup.ExecuteNonQuery();
    }
}
