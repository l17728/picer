using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Picer.Core.Identity;
using Picer.Core.Logging;

namespace Picer.App;

public static partial class AppServices
{
    private static CancellationTokenSource? _spaceNotificationCancellation;
    private static Task? _spaceNotificationWorker;
    private static readonly object SpaceNotificationGate = new();
    private static Func<Task>? _spaceNotificationRefresh;
    public static HttpClient? SpaceSynchronizationClient => ServiceMode ? AccountClient?.SpaceClient : _localSpacePublisherClient?.SpaceClient;

    public static void StartSpaceNotifications(Func<Task>? refresh = null, SpaceNotification? baseline = null)
    {
        lock (SpaceNotificationGate)
        {
        CancelSpaceNotifications();
        _spaceNotificationRefresh = refresh ?? _spaceNotificationRefresh;
        var client = SpaceSynchronizationClient;
        var account=ServiceMode ? AccountClient : _localSpacePublisherClient;
        if (client is null || account is null || !ServiceMode && !LocalSpaceSyncConfigured) return;
        string? library = null;
        if (!ServiceMode)
        {
            using var database = Db.OpenIndependentConnection();
            using var query = database.Command("SELECT library_id FROM library_identity WHERE id=1");
            library = (string)query.ExecuteScalar()!;
        }
        _spaceNotificationCancellation = new();
        var token = _spaceNotificationCancellation.Token;
        _spaceNotificationWorker = Task.Run(() => RunSpaceNotificationsAsync(account,library,baseline,token));
        }
    }

    private static void StopSpaceNotifications()
    {
        lock (SpaceNotificationGate) CancelSpaceNotifications();
    }

    private static Task? CancelSpaceNotifications()
    {
        _spaceNotificationCancellation?.Cancel();
        _spaceNotificationCancellation?.Dispose(); _spaceNotificationCancellation = null;
        // All exceptions are observed by the worker. Shutdown cancels its network
        // wait and page requests without blocking the WPF dispatcher.
        var worker=_spaceNotificationWorker; _spaceNotificationWorker=null;
        return worker;
    }

    public static Task StopSpaceNotificationsAsync()
    {
        lock (SpaceNotificationGate) return CancelSpaceNotifications()??Task.CompletedTask;
    }

    public static async Task<SpaceNotification?> CaptureSpaceNotificationBaselineAsync(CancellationToken token)
    {
        var client=SpaceSynchronizationClient;
        if (client is null || !ServiceMode && !LocalSpaceSyncConfigured) return null;
        string? library=null;
        if (!ServiceMode)
        {
            using var database=Db.OpenIndependentConnection();
            using var query=database.Command("SELECT library_id FROM library_identity WHERE id=1");
            library=query.ExecuteScalar() as string;
        }
        try
        {
            using var response=await client.PostAsJsonAsync("api/sync/v2/notifications",
                new SpaceNotificationRequest(library,WaitSeconds:0),DeviceAuthorization.Json,token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var result=await response.Content.ReadFromJsonAsync<SpaceNotification>(DeviceAuthorization.Json,token).ConfigureAwait(false);
            return result is { Revision:>=0 } && Guid.TryParseExact(result.Epoch,"N",out _) ? result : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException) { return null; }
    }

    private static async Task RunSpaceNotificationsAsync(DeviceAccountClient account, string? library, SpaceNotification? baseline, CancellationToken token)
    {
        var client=account.SpaceClient;
        var challenged=false;
        var after = baseline?.Revision??-1; var epoch = baseline?.Epoch; var failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var response = await client.PostAsJsonAsync("api/sync/v2/notifications",
                    new SpaceNotificationRequest(library,after,epoch),DeviceAuthorization.Json,token).ConfigureAwait(false);
                if (response.StatusCode==HttpStatusCode.Unauthorized)
                {
                    if (challenged) { AppLogger.Warn("照片空间通知无法重新授权，请重新登录。"); return; }
                    challenged=true;
                    var rejected=response.RequestMessage?.Headers.Authorization?.Parameter??account.Connection.Session?.AccessToken;
                    if (rejected is null) return;
                    try { await account.RefreshRejectedAccessAsync(rejected,token).ConfigureAwait(false); }
                    catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    { AppLogger.Warn("照片空间通知授权已撤销或失效，已停止通知连接。"); return; }
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    AppLogger.Warn(response.StatusCode==HttpStatusCode.NotFound
                        ? "服务尚未提供同步通知，已停止通知连接；可继续手动同步。"
                        : "照片空间通知授权已失效，已停止自动同步通知。"); return;
                }
                if (response.StatusCode == HttpStatusCode.Conflict && library is not null)
                {
                    var error = await response.Content.ReadFromJsonAsync<NotificationError>(DeviceAuthorization.Json,token).ConfigureAwait(false);
                    if (error?.Error == "source_not_registered")
                    {
                        await SyncLocalChangesToRemoteAsync(token).ConfigureAwait(false);
                        continue;
                    }
                }
                response.EnsureSuccessStatusCode();
                challenged=false;
                var notice = await response.Content.ReadFromJsonAsync<SpaceNotification>(DeviceAuthorization.Json,token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("space_notification_missing");
                if (!Guid.TryParseExact(notice.Epoch,"N",out _) || notice.Revision < 0 || notice.InspectionTargets is null ||
                    notice.InspectionTargets.Count > 100 || notice.InspectionTargets.Any(id => !Guid.TryParseExact(id,"N",out _)) ||
                    notice.InspectionTargets.Distinct().Count() != notice.InspectionTargets.Count ||
                    library is not null && !Guid.TryParseExact(notice.PublisherId,"N",out _))
                    throw new InvalidDataException("space_notification_invalid");
                if (notice.Epoch == epoch && notice.Revision == after && notice.InspectionTargets.Count == 0)
                { failures = 0; continue; }
                using var pass = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task? monitor = null;
                var sourcePathsAvailable=true;
                try
                {
                    if (notice.InspectionTargets.Count > 0)
                    {
                        var active = false;
                        foreach (var target in notice.InspectionTargets)
                            active |= await ReportInspectionAsync(client,notice,target,"running",pass.Token).ConfigureAwait(false);
                        if (!active) { pass.Cancel(); throw new OperationCanceledException(pass.Token); }
                        monitor = MonitorInspectionCancellationAsync(client,notice,pass);
                    }
                    else if (library is not null)
                        monitor = MonitorSourcePresenceAsync(client,library,notice,pass);
                    if (library is not null && notice.InspectionTargets.Count>0)
                    {
                        var scope=_localSpaceUploadScope??throw new InvalidOperationException("source_scope_unavailable");
                        var inspection=await Index.InspectReferencesAsync(scope.SourceIds,scope.Photos,scope.Videos,pass.Token).ConfigureAwait(false);
                        sourcePathsAvailable=inspection.Complete;
                    }
                    if (library is null)
                        await SyncRemoteMirrorAsync(pass.Token,requireMetadataSync:true,refreshMedia:true,publishPendingEdits:false).ConfigureAwait(false);
                    else
                        await SyncLocalChangesToRemoteAsync(pass.Token).ConfigureAwait(false);
                    if (notice.InspectionTargets.Count > 0)
                    {
                        using var database = Db.OpenIndependentConnection();
                        using var check = database.Command("PRAGMA quick_check");
                        if (!string.Equals(check.ExecuteScalar() as string,"ok",StringComparison.Ordinal))
                            throw new InvalidDataException("source_database_integrity_failed");
                        using var pending = database.Command("SELECT COUNT(*) FROM sync_pending_identity_adoptions WHERE publisher_id=$publisher");
                        pending.Parameters.AddWithValue("$publisher",notice.PublisherId!);
                        var pendingIdentities=Convert.ToInt64(pending.ExecuteScalar());
                        using var conflicts = database.Command("SELECT COUNT(*) FROM metadata_edit_conflicts WHERE state='pending'");
                        var pendingConflicts=Convert.ToInt64(conflicts.ExecuteScalar())+database.CountPendingCatalogSynchronization(notice.PublisherId);
                        var result = pendingIdentities==0 && pendingConflicts==0 && sourcePathsAvailable ? "completed" : "failed";
                        foreach (var target in notice.InspectionTargets)
                            await ReportInspectionAsync(client,notice,target,result,pass.Token,result=="completed" ? null :
                                !sourcePathsAvailable ? "source_paths_unavailable" : pendingIdentities>0 ? "pending_identity_adoptions" : "metadata_conflicts_pending").ConfigureAwait(false);
                    }
                    if (_spaceNotificationRefresh is { } refresh) await refresh().ConfigureAwait(false);
                    after = notice.Revision; epoch = notice.Epoch;
                    failures = 0;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (HttpRequestException exception) when (exception.StatusCode is null or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or
                    HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError or
                    HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ||
                    exception.StatusCode==HttpStatusCode.Conflict && failures<2) { throw; }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    foreach (var target in notice.InspectionTargets)
                    {
                        try { await ReportInspectionAsync(client,notice,target,"failed",token,
                            exception is InvalidDataException { Message:"source_database_integrity_failed" }
                                ? "source_database_integrity_failed" : "source_sync_failed").ConfigureAwait(false); }
                        catch (Exception receiptFailure) when (receiptFailure is HttpRequestException or OperationCanceledException) { }
                    }
                    throw;
                }
                finally
                {
                    pass.Cancel();
                    if (monitor is not null)
                        try { await monitor.ConfigureAwait(false); }
                        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                if (token.IsCancellationRequested) return;
                AppLogger.Warn("照片空间通知连接稍后重试；type="+exception.GetType().Name);
                failures = Math.Min(failures+1,5);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30,1<<failures)),token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static async Task<bool> ReportInspectionAsync(HttpClient client, SpaceNotification notice, string target,
        string result, CancellationToken token, string? errorCode = null)
    {
        return await new SpaceInspectionClient(client).ReportAsync(
            new SpaceInspectionReceipt(target,notice.PublisherId!,notice.Epoch,result,errorCode),token).ConfigureAwait(false);
    }

    private static async Task MonitorSourcePresenceAsync(HttpClient client,string library,SpaceNotification notice,CancellationTokenSource pass)
    {
        while (!pass.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15),pass.Token).ConfigureAwait(false);
            try
            {
                using var response=await client.PostAsJsonAsync("api/sync/v2/notifications",
                    new SpaceNotificationRequest(library,notice.Revision,notice.Epoch,0),DeviceAuthorization.Json,pass.Token).ConfigureAwait(false);
                if (response.StatusCode==HttpStatusCode.TooManyRequests) continue;
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException) { pass.Cancel(); throw; }
        }
    }

    private static async Task MonitorInspectionCancellationAsync(HttpClient client, SpaceNotification notice, CancellationTokenSource pass)
    {
        while (!pass.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5),pass.Token).ConfigureAwait(false);
            var active = false;
            try
            {
                foreach (var target in notice.InspectionTargets)
                    active |= await ReportInspectionAsync(client,notice,target,"running",pass.Token).ConfigureAwait(false);
                if (!active) { pass.Cancel(); return; }
            }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.TooManyRequests) { }
            catch (HttpRequestException) { pass.Cancel(); throw; }
        }
    }
    private sealed record NotificationError(string Error);
}
