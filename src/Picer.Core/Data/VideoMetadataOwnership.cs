using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    /// <summary>Legacy content tables are only a compatibility projection for one owner.</summary>
    public bool CanWriteLegacyVideoMetadata(long referenceId,SqliteTransaction? transaction=null)
    {
        using var query=Command("""
            SELECT COUNT(DISTINCT owner_key)=1 AND MIN(owner_key) NOT LIKE 'ambiguous-source:%' FROM sync_video_owners
            WHERE content_id=(SELECT content_id FROM videos WHERE id=$id)
            """);
        if (transaction is not null) query.Transaction=transaction;
        query.Parameters.AddWithValue("$id",referenceId); return Convert.ToInt64(query.ExecuteScalar())==1;
    }
}
