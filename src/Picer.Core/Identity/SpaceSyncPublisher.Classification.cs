using System.Net;
using System.Net.Http.Json;
using Picer.Core.Classification;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    public async Task PublishClassificationResultsAsync(SpaceUploadScope scope, IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (!scope.Photos) return;
        await _gate.WaitAsync(token);
        try
        {
            var session = accountClient.Connection.Session ?? throw new InvalidOperationException("device_login_required");
            using var db = localDatabase.OpenIndependentConnection();
            string publisher, epoch;
            using (var command = db.Command("SELECT publisher_id,epoch FROM sync_publishers WHERE device_grant_id=$grant AND space_id=$space AND incarnation_id=$device AND state='publishing'"))
            {
                command.Parameters.AddWithValue("$grant", session.GrantId); command.Parameters.AddWithValue("$space", session.SpaceId);
                command.Parameters.AddWithValue("$device", session.DeviceId);
                using var reader = command.ExecuteReader(); if (!reader.Read()) return;
                publisher = reader.GetString(0); epoch = reader.GetString(1);
            }
            var repository = new ClassificationRepository(db);
            var origin = accountClient.SpaceClient.BaseAddress!.AbsoluteUri + "classification-publisher/" + publisher;
            foreach (var result in repository.ExportResults(publisher, epoch, scope))
                repository.QueueEdit(origin, "publisher-result", result.OperationId, ClassificationRepository.Encode(result));
            var accepted = 0; var deferred = 0;
            // Existing conflicts can become applicable after metadata, definitions or model settings converge.
            foreach (var edit in repository.PendingSnapshot(origin))
            {
                token.ThrowIfCancellationRequested();
                var transfer = ClassificationRepository.Decode<ClassificationTransfer>(edit.Payload);
                if (transfer.Reference.Epoch != epoch || !transfer.Reference.Scope.SourceIds.All(scope.SourceIds.Contains)
                    || transfer.Input.People.Count > 0 && !scope.People) { deferred++; continue; }
                using var response = await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/classification-results", transfer, DeviceAuthorization.Json, token);
                if (response.StatusCode == HttpStatusCode.NotFound) { progress?.Report("远程服务尚不支持分类结果同步，本地结果已保留。"); return; }
                if (response.StatusCode == HttpStatusCode.Conflict) { repository.MarkEdit(edit.OperationId, "conflict"); deferred++; continue; }
                response.EnsureSuccessStatusCode();
                var receipt = await response.Content.ReadFromJsonAsync<ClassificationTransferReceipt>(DeviceAuthorization.Json, token)
                    ?? throw new InvalidDataException("classification_receipt_missing");
                if (receipt.OperationId != edit.OperationId) throw new InvalidDataException("classification_receipt_mismatch");
                repository.MarkEdit(edit.OperationId, "acknowledged");
                using var readback = await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/classification-results/read",
                    new ClassificationTransferRead(edit.OperationId, transfer.Reference), DeviceAuthorization.Json, token);
                readback.EnsureSuccessStatusCode();
                var confirmed = await readback.Content.ReadFromJsonAsync<ClassificationTransferReceipt>(DeviceAuthorization.Json, token);
                if (confirmed != receipt) throw new InvalidDataException("classification_readback_mismatch");
                repository.MarkEdit(edit.OperationId, "completed"); accepted++;
            }
            if (accepted + deferred > 0) progress?.Report($"分类结果同步：已确认 {accepted}，待配置/证据收敛 {deferred}；本地结果保留。");
        }
        finally { _gate.Release(); }
    }
}
