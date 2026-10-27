using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    private static void PrepareDownloadScope(PicerDatabase database,string publisher,SpaceUploadScope scope)
    {
        var hash=DeviceAuthorization.Digest(JsonSerializer.Serialize(scope with { SourceIds=scope.SourceIds.Distinct().Order().ToArray() },DeviceAuthorization.Json));
        using var update=database.Command("""
            UPDATE sync_publishers SET downloaded_sequence=0,downloaded_metadata_version=0,downloaded_metadata_operation='',downloaded_scope_hash=$scope
            WHERE publisher_id=$publisher AND downloaded_scope_hash<>$scope
            """);
        update.Parameters.AddWithValue("$publisher",publisher); update.Parameters.AddWithValue("$scope",hash); update.ExecuteNonQuery();
    }

    private async Task<int> PullPagedMetadataEditsAsync(PicerDatabase database,string publisher,string epoch,SpaceUploadScope? scope,
        IProgress<string>? progress,CancellationToken cancellationToken)
    {
        long after; string operation;
        using (var query=database.Command("SELECT downloaded_metadata_version,downloaded_metadata_operation FROM sync_publishers WHERE publisher_id=$publisher"))
        {
            query.Parameters.AddWithValue("$publisher",publisher); using var row=query.ExecuteReader();
            if (!row.Read()) throw new InvalidDataException("metadata_change_publisher_missing");
            after=row.GetInt64(0); operation=row.GetString(1);
        }
        long? through=null; var total=0;
        do
        {
            var path=$"api/sync/v2/metadata-edit-page?after={after}&operation={Uri.EscapeDataString(operation)}&epoch={epoch}&pageSize=500"+
                (through is { } cut?$"&through={cut}":"");
            using var response=await accountClient.SpaceClient.GetAsync(path,cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var page=await response.Content.ReadFromJsonAsync<SpaceMetadataEditPage>(DeviceAuthorization.Json,cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("metadata_edit_page_missing");
            if (page.PublisherId!=publisher || page.Epoch!=epoch || through is { } expected && page.Through!=expected ||
                page.Through<after || page.Latest<page.Through || page.NextVersion>page.Through || page.NextVersion<after || page.NextOperation is null ||
                page.NextOperation is not ("" or "~") && !Guid.TryParseExact(page.NextOperation,"N",out _) ||
                page.NextVersion==after && StringComparer.Ordinal.Compare(page.NextOperation,operation)<0 ||
                page.HasMore && page.NextVersion==after && page.NextOperation==operation ||
                page.HasMore && !Guid.TryParseExact(page.NextOperation,"N",out _) ||
                !page.HasMore && (page.NextVersion!=page.Through || page.NextOperation!="~") ||
                page.Edits is null || page.Edits.Count>500 || page.Edits.Any(edit=>edit is null || edit.ServerVersion<=0 ||
                    edit.ServerVersion<after || edit.ServerVersion==after && StringComparer.Ordinal.Compare(edit.OperationId,operation)<=0 ||
                    edit.ServerVersion>page.NextVersion || edit.ServerVersion==page.NextVersion && StringComparer.Ordinal.Compare(edit.OperationId,page.NextOperation)>0))
                throw new InvalidDataException("metadata_edit_page_invalid");
            through=page.Through;
            using (var transaction=database.BeginTransaction())
            {
                RequireDownloadPublisher(database,publisher,epoch,transaction);
                total+=MergeDownloadedEdits(database,publisher,page.Edits,scope,transaction);
                using var update=database.Command("""
                    UPDATE sync_publishers SET downloaded_metadata_version=$next,downloaded_metadata_operation=$operation
                    WHERE publisher_id=$publisher AND downloaded_metadata_version=$before AND downloaded_metadata_operation=$previous
                    """);
                update.Transaction=transaction; update.Parameters.AddWithValue("$next",page.NextVersion); update.Parameters.AddWithValue("$operation",page.NextOperation);
                update.Parameters.AddWithValue("$publisher",publisher); update.Parameters.AddWithValue("$before",after); update.Parameters.AddWithValue("$previous",operation);
                if (update.ExecuteNonQuery()!=1) throw new InvalidDataException("metadata_edit_cursor_changed");
                transaction.Commit();
            }
            after=page.NextVersion; operation=page.NextOperation;
            progress?.Report($"正在同步人工元数据：已应用 {total} 项…");
            if (!page.HasMore)
            {
                if (page.Latest<=page.Through) return total;
                through=null;
            }
        } while (true);
    }

    private static void RequireDownloadPublisher(PicerDatabase database,string publisher,string epoch,SqliteTransaction transaction)
    {
        using var query=database.Command("SELECT 1 FROM sync_publishers WHERE publisher_id=$publisher AND epoch=$epoch AND state='publishing'");
        query.Transaction=transaction; query.Parameters.AddWithValue("$publisher",publisher); query.Parameters.AddWithValue("$epoch",epoch);
        if (query.ExecuteScalar() is null) throw new InvalidDataException("metadata_change_publisher_epoch_mismatch");
    }

    private static int MergeDownloadedEdits(PicerDatabase database,string publisher,IReadOnlyList<MetadataEdit> edits,SpaceUploadScope? scope,SqliteTransaction transaction)
    {
        foreach (var edit in edits) PicerDatabase.ValidateMetadataEdit(edit);
        var authorized=edits.Where(edit=>IsEditInScope(database,transaction,edit,scope))
            .Select(edit=>TranslateAdoptedPersonEdit(database,transaction,publisher,edit)).ToArray();
        var imported=authorized.GroupBy(edit=>edit.OperationId,StringComparer.Ordinal)
            .ToDictionary(group=>group.Key,group=>(group.First().Kind,group.First().EntityId,group.First().Field),StringComparer.Ordinal);
        foreach (var pending in database.GetPendingMetadataEdits(transaction))
            if (imported.TryGetValue(DeviceAuthorization.Digest(publisher+":"+pending.OperationId)[..32],out var owner)) imported[pending.OperationId]=owner;
        Execute(database,transaction,"INSERT OR IGNORE INTO sync_suppression(id) VALUES(1)");
        database.MergeMetadataEditsInTransaction(authorized,transaction,applyOnlyChangedFields:true,importedPublisher:publisher);
        using var mark=database.Command("""
            INSERT INTO sync_imported_metadata_edits(publisher_id,operation_id,entity_kind,entity_id,entity_field) VALUES($publisher,$operation,$kind,$entity,$field)
            ON CONFLICT(publisher_id,operation_id) DO UPDATE SET entity_kind=excluded.entity_kind,entity_id=excluded.entity_id,entity_field=excluded.entity_field
            """);
        mark.Transaction=transaction;
        foreach (var (id,owner) in imported)
        {
            mark.Parameters.Clear(); mark.Parameters.AddWithValue("$publisher",publisher); mark.Parameters.AddWithValue("$operation",id);
            mark.Parameters.AddWithValue("$kind",owner.Kind);mark.Parameters.AddWithValue("$entity",owner.EntityId);
            mark.Parameters.AddWithValue("$field",owner.Field);mark.ExecuteNonQuery();
        }
        Execute(database,transaction,"DELETE FROM sync_suppression WHERE id=1");
        return authorized.Length;
    }

    private async Task PullMetadataOperationStatesAsync(PicerDatabase database,string publisher,string epoch,SpaceUploadScope? scope,CancellationToken cancellationToken)
    {
        MetadataEdit[] pending;
        using (var snapshot=database.BeginReadTransaction())
            pending=database.GetPendingMetadataEdits(snapshot).Where(edit=>IsEditInScope(database,snapshot,edit,scope)).ToArray();
        foreach (var batch in pending.Chunk(500))
        {
            using var response=await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/metadata-operation-states",
                new SpaceArchivedOperationRequest(publisher,epoch,batch.Select(edit=>edit.OperationId).ToArray()),cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var states=await response.Content.ReadFromJsonAsync<SpaceMetadataOperationResponse>(DeviceAuthorization.Json,cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("metadata_operation_states_missing");
            var sent=batch.Select(edit=>edit.OperationId).ToHashSet(StringComparer.Ordinal);
            if (states.PublisherId!=publisher || states.Epoch!=epoch || states.Operations is null || states.Operations.Count>batch.Length ||
                states.Operations.Any(item=>item is null || !sent.Contains(item.OperationId) || item.Current is null || item.Current.ServerVersion<=0) ||
                states.Operations.Select(item=>item.OperationId).Distinct(StringComparer.Ordinal).Count()!=states.Operations.Count)
                throw new InvalidDataException("metadata_operation_states_invalid");
            using var transaction=database.BeginTransaction(); RequireDownloadPublisher(database,publisher,epoch,transaction);
            MergeDownloadedEdits(database,publisher,states.Operations.Select(item=>item.Current).ToArray(),scope,transaction);
            transaction.Commit();
        }
    }
}
