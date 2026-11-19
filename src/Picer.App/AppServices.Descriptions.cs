using Picer.App.Infrastructure;
using Picer.Core.Indexing;

namespace Picer.App;

public static partial class AppServices
{
    private static readonly CoalescingSyncWorker DescriptionWorker = new(SyncDescriptionsAsync);

    public static void QueueDescriptionSync()
    {
        if (SyncClient is null || !Db.EnableDescriptionSync) { QueueImmediateRemoteSync("description committed"); return; }
        DescriptionWorker.Request();
    }

    private static async Task SyncDescriptionsAsync(CancellationToken token)
    {
        if (SyncClient is not { } client) return;
        try { await client.PushDescriptionsAsync(Db, token).ConfigureAwait(false); }
        catch (InvalidOperationException exception) when (exception.Message == "description_sync_conflicts_pending")
        { Picer.Core.Logging.AnalysisDiagnosticLog.Warn("Description sync conflicts retained; accepted results will be confirmed"); }
        if (IsDescriptionRunning)
        {
            Picer.Core.Logging.AnalysisDiagnosticLog.Info("Description mirror pull deferred while directory description is running");
            return;
        }
        await RemoteMirrorGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            try
            {
                if (await client.PullDescriptionChangesAsync(Db, token).ConfigureAwait(false)) return;
            }
            catch (InvalidOperationException exception) when (exception.Message is "description_delta_baseline_required" or "description_delta_media_refresh_required")
            {
                Volatile.Write(ref _remoteMediaBootstrapped, false);
                await SyncRemoteMirrorCoreAsync(token, requireMetadataSync: true, publishPendingEdits: false).ConfigureAwait(false);
                await UiWork.RunDatabaseAsync("Descriptions.ResetCursor", () =>
                {
                    using var connection = Db.OpenIndependentConnection();
                    using var clear = connection.Command("DELETE FROM description_delta_checkpoint"); clear.ExecuteNonQuery();
                }, token).ConfigureAwait(false);
                await client.PullDescriptionChangesAsync(Db, token).ConfigureAwait(false);
                return;
            }
            await SyncRemoteMirrorCoreAsync(token, requireMetadataSync: true, publishPendingEdits: false).ConfigureAwait(false);
        }
        finally { RemoteMirrorGate.Release(); }
    }
}
