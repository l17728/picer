using System.Net.Http.Json;
using Picer.Core.Data;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    private async Task PullConflictResolutionsAsync(PicerDatabase database,string publisher,string epoch,
        SpaceUploadScope? scope,CancellationToken cancellationToken)
    {
        MetadataEditConflict[] candidates;
        using (var selection=database.BeginReadTransaction())
            candidates=database.GetMetadataEditConflicts(selection)
                .Where(conflict=>IsEditInScope(database,selection,conflict.Proposed,scope)).ToArray();
        foreach (var batch in candidates.Chunk(500))
        {
            using var response=await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/conflict-resolutions",
                new SpaceArchivedOperationRequest(publisher,epoch,batch.Select(item=>item.OperationId).ToArray()),cancellationToken).ConfigureAwait(false);
            if (response.StatusCode==System.Net.HttpStatusCode.NotFound) return;
            response.EnsureSuccessStatusCode();
            var receipt=await response.Content.ReadFromJsonAsync<SpaceConflictResolutionResponse>(DeviceAuthorization.Json,cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("metadata_conflict_receipt_missing");
            var sent=batch.Select(item=>item.OperationId).ToHashSet(StringComparer.Ordinal);
            if (receipt.PublisherId!=publisher || receipt.Epoch!=epoch || receipt.Resolutions is null ||
                receipt.Resolutions.Count>batch.Length || receipt.Resolutions.Any(item=>item is null || !sent.Contains(item.OperationId) || item.Version<=0) ||
                receipt.Resolutions.Select(item=>item.OperationId).Distinct(StringComparer.Ordinal).Count()!=receipt.Resolutions.Count)
                throw new InvalidDataException("metadata_conflict_receipt_invalid");
            using var transaction=database.BeginTransaction();
            using var fence=database.Command("SELECT 1 FROM sync_publishers WHERE publisher_id=$publisher AND epoch=$epoch AND state='publishing'");
            fence.Transaction=transaction; fence.Parameters.AddWithValue("$publisher",publisher); fence.Parameters.AddWithValue("$epoch",epoch);
            if (fence.ExecuteScalar() is null) throw new InvalidDataException("metadata_change_publisher_epoch_mismatch");
            foreach (var resolution in receipt.Resolutions)
                if (IsEditInScope(database,transaction,batch.Single(item=>item.OperationId==resolution.OperationId).Proposed,scope))
                    database.ApplyMetadataConflictReceipt(resolution,transaction);
            transaction.Commit();
        }
    }
}
