using System.Net.Http.Json;
using Picer.Core.Data;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    private async Task QuarantineArchivedOperationsAsync(PicerDatabase database, string publisherId, string epoch,
        SpaceUploadScope? scope, CancellationToken cancellationToken)
    {
        if (scope is { Videos:false }) return;
        MetadataEdit[] candidates;
        using (var selection = database.BeginReadTransaction())
            candidates = database.GetVideoMetadataEdits(selection).Where(edit => IsEditInScope(database, selection, edit, scope)).ToArray();
        foreach (var batch in candidates.Chunk(500))
        {
            using var response = await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/archived-operations",
                new SpaceArchivedOperationRequest(publisherId, epoch, batch.Select(edit => edit.OperationId).ToArray()), cancellationToken).ConfigureAwait(false);
            // Older servers have no ownership quarantine and no such endpoint.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return;
            response.EnsureSuccessStatusCode();
            var receipt = await response.Content.ReadFromJsonAsync<SpaceArchivedOperationResponse>(DeviceAuthorization.Json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("archived_operation_response_missing");
            var sent = batch.Select(edit => edit.OperationId).ToHashSet(StringComparer.Ordinal);
            if (receipt.PublisherId != publisherId || receipt.Epoch != epoch || receipt.OperationIds is null ||
                receipt.OperationIds.Count > batch.Length || receipt.OperationIds.Any(id => !sent.Contains(id)))
                throw new InvalidDataException("archived_operation_response_invalid");
            using var transaction = database.BeginTransaction();
            using var fence = database.Command("SELECT 1 FROM sync_publishers WHERE publisher_id=$publisher AND epoch=$epoch AND state='publishing'");
            fence.Transaction = transaction; fence.Parameters.AddWithValue("$publisher", publisherId); fence.Parameters.AddWithValue("$epoch", epoch);
            if (fence.ExecuteScalar() is null) throw new InvalidDataException("metadata_change_publisher_epoch_mismatch");
            Execute(database, transaction, "INSERT OR IGNORE INTO sync_suppression(id) VALUES(1)");
            foreach (var operation in receipt.OperationIds.Distinct(StringComparer.Ordinal))
            {
                var proposed = batch.Single(edit => edit.OperationId == operation);
                if (IsEditInScope(database, transaction, proposed, scope))
                    database.QuarantineVideoMetadataOperation(operation, transaction);
            }
            Execute(database, transaction, "DELETE FROM sync_suppression WHERE id=1");
            transaction.Commit();
        }
    }
}
