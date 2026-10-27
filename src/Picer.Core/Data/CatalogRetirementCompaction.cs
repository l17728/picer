using Microsoft.Data.Sqlite;
using Picer.Core.Identity;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    public IReadOnlyList<CatalogRetirementManifest> GetCatalogCompactionCandidates(string publisher,SpaceUploadScope scope,string after="",int limit=100)
    {
        if(limit is <1 or >100 || after is null || after.Length>32) throw new ArgumentException("invalid_catalog_compaction_cursor");
        using var query=Command("""
            SELECT operation_id,kind,global_id,confirmed_media_count FROM sync_retired_catalog_identities
            WHERE publisher_id=$publisher AND compacted=0 AND operation_id>$after
                AND ($photos=1 OR $videos=1) AND (kind<>'person' OR $people=1) AND (kind<>'album' OR $photos=1)
            ORDER BY operation_id LIMIT $limit
            """);
        query.Parameters.AddWithValue("$publisher",publisher);query.Parameters.AddWithValue("$after",after);
        query.Parameters.AddWithValue("$limit",limit);query.Parameters.AddWithValue("$photos",scope.Photos?1:0);
        query.Parameters.AddWithValue("$videos",scope.Videos?1:0);query.Parameters.AddWithValue("$people",scope.People?1:0);
        using var rows=query.ExecuteReader();var result=new List<CatalogRetirementManifest>();
        while(rows.Read()) result.Add(new(rows.GetString(0),rows.GetString(1),rows.GetString(2),rows.GetInt64(3)));
        return result;
    }

    // Called only after the peer has persisted its own completed marker. This is
    // an explicit acknowledgement barrier, never a time-based deletion policy.
    public CatalogRetirementCompletion CompactCatalogRetirement(string publisher,CatalogRetirementManifest manifest,
        SpaceUploadScope scope,SqliteTransaction transaction)
    {
        using(var confirmed=Command("SELECT 1 FROM sync_retired_catalog_identities WHERE publisher_id=$publisher AND kind=$kind AND global_id=$global"))
        {
            confirmed.Transaction=transaction;confirmed.Parameters.AddWithValue("$publisher",publisher);
            confirmed.Parameters.AddWithValue("$kind",manifest.Kind);confirmed.Parameters.AddWithValue("$global",manifest.GlobalId);
            if(confirmed.ExecuteScalar() is null) return new("pending",false);
        }
        var completion=FinalizeCatalogRetirement(publisher,manifest.OperationId,manifest.Kind,manifest.GlobalId,manifest.MediaCount,scope,transaction);
        if(completion.State!="completed") return completion;
        using var compact=Command("""
            INSERT OR IGNORE INTO sync_catalog_retirement_predecessors(publisher_id,kind,global_id,field_operation_id)
                SELECT r.publisher_id,r.kind,r.global_id,op.value FROM sync_catalog_retirement_receipts r,json_each(r.applied_operations) op
                WHERE r.publisher_id=$publisher AND r.operation_id=$operation AND r.kind=$kind AND r.global_id=$global
                    AND r.state IN ('applied','delivered') AND EXISTS(SELECT 1 FROM metadata_edits e WHERE e.operation_id=op.value);
            INSERT OR IGNORE INTO sync_catalog_retirement_predecessors(publisher_id,kind,global_id,field_operation_id)
                SELECT r.publisher_id,r.kind,r.global_id,op.value FROM sync_catalog_completion_reviews r,json_each(r.applied_operations) op
                WHERE r.publisher_id=$publisher AND r.operation_id=$operation AND r.kind=$kind AND r.global_id=$global
                    AND r.state='applied' AND EXISTS(SELECT 1 FROM metadata_edits e WHERE e.operation_id=op.value);
            DELETE FROM sync_catalog_retirement_receipts WHERE publisher_id=$publisher AND operation_id=$operation;
            DELETE FROM sync_catalog_completion_reviews WHERE publisher_id=$publisher AND operation_id=$operation;
            DELETE FROM sync_catalog_retirements WHERE publisher_id=$publisher AND operation_id=$operation AND state='completed';
            DELETE FROM sync_catalog_purged_receipt_counts WHERE publisher_id=$publisher AND operation_id=$operation;
            UPDATE sync_retired_catalog_identities SET compacted=1
                WHERE publisher_id=$publisher AND kind=$kind AND global_id=$global AND compacted=0;
            """);
        compact.Transaction=transaction;compact.Parameters.AddWithValue("$publisher",publisher);compact.Parameters.AddWithValue("$operation",manifest.OperationId);
        compact.Parameters.AddWithValue("$kind",manifest.Kind);compact.Parameters.AddWithValue("$global",manifest.GlobalId);compact.ExecuteNonQuery();
        return completion;
    }
}
