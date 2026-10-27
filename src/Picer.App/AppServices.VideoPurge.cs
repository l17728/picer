using System.IO;
using Picer.App.Infrastructure;
using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Video;

namespace Picer.App;

public static partial class AppServices
{
    public static async Task PurgeVideoAsync(VideoRecord record, CancellationToken token = default)
    {
        await Services.MediaPlayback.StopAllAsync();
        using var removal = await VideoOperationLifetime.BeginPurgeAsync(record.Path, token);
        using var mutation = await AcquireIndexMutationAsync(token);
        if(!ServiceMode && _localSpacePublisher is not null && _localSpaceUploadScope is not null)
            await _localSpacePublisher.PurgePublishedReferenceAsync("video",record.Id,_localSpaceUploadScope,token);
        else if (!ServiceMode && SyncGateway is not null)
        {
            await SyncLocalChangesToRemoteCoreAsync(token);
            string? cursor = null;
            do
            {
                var page = await SyncGateway.ListVideosAsync(pageSize: 500, cursor: cursor, cancellationToken: token);
                var remote = page.Items.FirstOrDefault(video => video.Sha256 == record.Sha256 &&
                    DirectoryMediaImport.Normalize(video.Path).Equals(DirectoryMediaImport.Normalize(record.Path), StringComparison.OrdinalIgnoreCase));
                if (remote is not null)
                {
                    if (!await SyncGateway.PurgeVideoAsync(remote.Id, token)) throw new InvalidOperationException("服务端未完成清除，请重试。");
                    break;
                }
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        if (ServiceMode && !await Gateway.PurgeVideoAsync(record.Id, token))
            throw new InvalidOperationException("服务端未完成清除，请重试。");
        await UiWork.RunDatabaseAsync(() => new VideoRecordPurger(Db, ServiceMode ? VideoPurgeCacheScope.Remote : VideoPurgeCacheScope.Local).Purge(record.Id), token);
        if (ServiceMode && File.Exists(AppPaths.DbPath))
            await UiWork.RunDatabaseAsync(() =>
            {
                using var local = new PicerDatabase(AppPaths.DbPath);
                var old = new VideoRepository(local).GetAll().FirstOrDefault(video => video.Sha256 == record.Sha256 &&
                    DirectoryMediaImport.Normalize(video.Path).Equals(DirectoryMediaImport.Normalize(record.Path), StringComparison.OrdinalIgnoreCase));
                if (old is not null) new VideoRecordPurger(local).Purge(old.Id);
            }, token);
        // No publication until the authoritative deletion is reflected locally.
        mutation.RestoreUnchangedMirror();
    }
}
