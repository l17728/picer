using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    public void StageCatalogCompletions(string publisher,string epoch,IReadOnlyList<CatalogRetirementManifest> items,SqliteTransaction transaction)
    {
        if(items is null || items.Count>100 || !Guid.TryParseExact(epoch,"N",out _)) throw new ArgumentException("invalid_catalog_completion_inbox");
        using(var owner=Command("SELECT 1 FROM sync_publishers WHERE publisher_id=$publisher AND epoch=$epoch AND state='publishing'"))
        {
            owner.Transaction=transaction;owner.Parameters.AddWithValue("$publisher",publisher);owner.Parameters.AddWithValue("$epoch",epoch);
            if(owner.ExecuteScalar() is null) throw new InvalidDataException("catalog_completion_publisher_changed");
        }
        foreach(var item in items)
        {
            if(item is null || !Guid.TryParseExact(item.OperationId,"N",out _) || !Guid.TryParseExact(item.GlobalId,"N",out _)
                || item.Kind is not ("tag" or "album" or "person") || item.MediaCount<0) throw new ArgumentException("invalid_catalog_completion_manifest");
            using(var prior=Command("SELECT epoch,kind,global_id,media_count FROM sync_catalog_completion_inbox WHERE publisher_id=$publisher AND operation_id=$operation"))
            {
                prior.Transaction=transaction;prior.Parameters.AddWithValue("$publisher",publisher);prior.Parameters.AddWithValue("$operation",item.OperationId);
                using var row=prior.ExecuteReader();
                if(row.Read())
                {
                    if(row.GetString(0)!=epoch || row.GetString(1)!=item.Kind || row.GetString(2)!=item.GlobalId || row.GetInt64(3)!=item.MediaCount)
                        throw new InvalidDataException("catalog_completion_payload_changed");
                    continue;
                }
            }
            using var save=Command("""
                INSERT INTO sync_catalog_completion_inbox(publisher_id,operation_id,epoch,kind,global_id,media_count)
                VALUES($publisher,$operation,$epoch,$kind,$global,$count)
                """);
            save.Transaction=transaction;save.Parameters.AddWithValue("$publisher",publisher);save.Parameters.AddWithValue("$operation",item.OperationId);
            save.Parameters.AddWithValue("$epoch",epoch);save.Parameters.AddWithValue("$kind",item.Kind);
            save.Parameters.AddWithValue("$global",item.GlobalId);save.Parameters.AddWithValue("$count",item.MediaCount);save.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<CatalogRetirementManifest> GetPendingCatalogCompletions(string publisher,string epoch,string after="",int limit=100)
    {
        if(limit is <1 or >100 || after is null || after.Length>32) throw new ArgumentException("invalid_catalog_completion_cursor");
        using var query=Command("""
            SELECT operation_id,kind,global_id,media_count FROM sync_catalog_completion_inbox
            WHERE publisher_id=$publisher AND epoch=$epoch AND operation_id>$after ORDER BY operation_id LIMIT $limit
            """);
        query.Parameters.AddWithValue("$publisher",publisher);query.Parameters.AddWithValue("$epoch",epoch);
        query.Parameters.AddWithValue("$after",after);query.Parameters.AddWithValue("$limit",limit);
        using var rows=query.ExecuteReader();var result=new List<CatalogRetirementManifest>();
        while(rows.Read()) result.Add(new(rows.GetString(0),rows.GetString(1),rows.GetString(2),rows.GetInt64(3)));
        return result;
    }

    public void RecordCatalogCompletionInboxResult(string publisher,string epoch,string operation,CatalogRetirementCompletion result,
        bool remoteCompleted,SqliteTransaction transaction)
    {
        if(result.State is not ("completed" or "pending" or "conflict" or "scope_conflict") || result.State=="completed"&&!remoteCompleted)
            throw new ArgumentException("invalid_catalog_completion_result");
        using var record=Command(result.State=="completed"?"""
            DELETE FROM sync_catalog_completion_inbox WHERE publisher_id=$publisher AND epoch=$epoch AND operation_id=$operation
            """:"""
            UPDATE sync_catalog_completion_inbox SET state=$state,remote_completed=MAX(remote_completed,$remote)
            WHERE publisher_id=$publisher AND epoch=$epoch AND operation_id=$operation
            """);
        record.Transaction=transaction;record.Parameters.AddWithValue("$publisher",publisher);record.Parameters.AddWithValue("$epoch",epoch);
        record.Parameters.AddWithValue("$operation",operation);record.Parameters.AddWithValue("$state",result.State);
        record.Parameters.AddWithValue("$remote",remoteCompleted?1:0);record.ExecuteNonQuery();
    }
}
