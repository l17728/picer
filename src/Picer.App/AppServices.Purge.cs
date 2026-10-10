using System.IO;
using Picer.App.Infrastructure;
using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Models;

namespace Picer.App;

public static partial class AppServices
{
    internal static async Task<IndexMutationLease> AcquireIndexMutationAsync(CancellationToken token)
    {
        await RemoteMirrorGate.WaitAsync(token);
        var ready = Volatile.Read(ref _remoteMediaBootstrapped);
        if (ServiceMode) Volatile.Write(ref _remoteMediaBootstrapped, false);
        return new IndexMutationLease(ready);
    }

    internal sealed class IndexMutationLease(bool wasReady) : IDisposable
    {
        public bool RestoreUnchangedMirror()
        {
            if (ServiceMode && wasReady) Volatile.Write(ref _remoteMediaBootstrapped, true);
            return ServiceMode && wasReady;
        }
        public void Dispose() => RemoteMirrorGate.Release();
    }

    // Caller holds the mutation lease. A failed/canceled refresh leaves the
    // mirror unbootstrapped, so background publication refreshes before sending.
    internal static Task RefreshMirrorAfterIndexMutationAsync(CancellationToken token)
    {
        if (ServiceMode) return SyncRemoteMirrorCoreAsync(token, requireMetadataSync: true);
        QueueImmediateRemoteSync("索引位置修复");
        return Task.CompletedTask;
    }

    public static async Task PurgeMissingImageAsync(ImageRecord record)
    {
        await Services.MediaPlayback.StopAllAsync();
        await RemoteMirrorGate.WaitAsync();
        try
        {
            if (!ServiceMode && SyncGateway is not null)
            {
                // Publish the latest missing state first, then remove this exact
                // reference from the paired service under the same sync gate.
                await SyncLocalChangesToRemoteCoreAsync();
                string? cursor = null;
                do
                {
                    var page = await SyncGateway.ListImagesAsync(includeMissing: true, pageSize: 500, cursor: cursor);
                    var remote = page.Items.FirstOrDefault(image => image.Sha256 == record.Sha256 &&
                        DirectoryMediaImport.Normalize(image.Path).Equals(DirectoryMediaImport.Normalize(record.Path), StringComparison.OrdinalIgnoreCase));
                    if (remote is not null)
                    {
                        if (!await SyncGateway.PurgeMissingImageAsync(remote.Id)) throw new InvalidOperationException("服务端记录状态已变化，请刷新后重试。");
                        break;
                    }
                    cursor = page.NextCursor;
                } while (cursor is not null);
            }
            if (ServiceMode && !await Gateway.PurgeMissingImageAsync(record.Id))
                throw new InvalidOperationException("服务端记录状态已变化，请刷新后重试。");
            if (!await UiWork.RunDatabaseAsync(() => new ImageRecordPurger(Db).Purge(record.Id)))
                throw new InvalidOperationException("记录已恢复可用，请刷新后重试。");
            // The same source host may keep its pre-service local database.
            // Remove the matching old reference there as well to prevent re-sync.
            if (ServiceMode && File.Exists(AppPaths.DbPath))
                await UiWork.RunDatabaseAsync(() =>
                {
                    using var local = new PicerDatabase(AppPaths.DbPath);
                    var localRecord = local.GetImageByPath(IndexRepairService.LocalPath(record.Path), true);
                    if (localRecord is not null && localRecord.Sha256 == record.Sha256)
                        new ImageRecordPurger(local).Purge(localRecord.Id, requireMissing: false);
                });
        }
        finally { RemoteMirrorGate.Release(); }
    }
}
