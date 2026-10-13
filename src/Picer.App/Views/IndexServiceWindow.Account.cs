using System.IO;
using System.Text.Json;
using System.Windows;
using System.Net.Http.Json;
using Picer.Core.Identity;

namespace Picer.App.Views;

public partial class IndexServiceWindow
{
    private CancellationTokenSource? _uploadCancellation;

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.ServiceMode || AppServices.Db is null || _uploadCancellation is not null)
        { HealthText.Text = "请在本地模式中上传本机索引。"; return; }
        var connection = new AccountConnectionWindow(AppPaths.AccountCredentialPath, forUpload: true) { Owner = this };
        if (connection.ShowDialog() != true || connection.Result?.Session is not { } session) return;
        var preferences = Path.Combine(AppPaths.DataRoot, "upload-preferences",
            DeviceAuthorization.Digest(connection.Result.ServiceId + ":" + session.AccountId + ":" + session.SpaceId) + ".json");
        SpaceUploadScope? previous = null;
        try
        {
            if (File.Exists(preferences)) previous = JsonSerializer.Deserialize<SpaceUploadScope>(File.ReadAllText(preferences));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
        var selection = new SpaceUploadScopeWindow(AppServices.Db.GetSources(), session, previous) { Owner = this };
        if (selection.ShowDialog() != true || selection.Result is not { } scope) return;
        using var cancellation = new CancellationTokenSource();
        _uploadCancellation = cancellation;
        SyncButton.IsEnabled = false;
        PauseSyncButton.Visibility = Visibility.Visible;
        EventHandler closed = (_, _) => cancellation.Cancel();
        Closed += closed;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preferences)!);
            var temporary = preferences + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(scope));
                File.Move(temporary, preferences, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            using var client = new WindowsDeviceCredentialStore(AppPaths.AccountCredentialPath).OpenClient();
            var publisher = new SpaceSyncPublisher(client, AppServices.Db, Path.Combine(AppPaths.DataRoot, "upload-snapshots"));
            var progress = new Progress<string>(message => HealthText.Text = message);
            await publisher.PublishAsync(progress, cancellation.Token, scope);
            HealthText.Text = $"索引已上传到“{session.SpaceName}”。其他电脑登录同一账号并选择此空间即可查看。";
            if (selection.ServeMedia)
            {
                try { await ConnectSourceAsync(client, scope, cancellation.Token); }
                catch (OperationCanceledException) { AgentStatusText.Text = "索引已上传。来源设备设置已取消，可稍后重新连接。"; }
                catch (InvalidOperationException exception) { AgentStatusText.Text = exception.Message; }
                catch { AgentStatusText.Text = "索引已上传，但来源设备尚未连接。请检查驻留程序的网络和授权后重试。"; }
            }
        }
        catch (OperationCanceledException) { HealthText.Text = "上传已暂停。再次选择相同范围上传，会从已确认的批次继续。"; }
        catch (InvalidOperationException exception) { HealthText.Text = exception.Message; }
        catch { HealthText.Text = "上传未完成，已保留进度。请检查网络和设备授权，再以相同范围重试。"; }
        finally
        {
            Closed -= closed;
            _uploadCancellation = null;
            SyncButton.IsEnabled = true;
            PauseSyncButton.Visibility = Visibility.Collapsed;
        }
    }

    private void PauseSync_Click(object sender, RoutedEventArgs e) => _uploadCancellation?.Cancel();

    private async Task ConnectSourceAsync(DeviceAccountClient gui, SpaceUploadScope scope, CancellationToken cancellationToken)
    {
        var dialog = new AccountConnectionWindow(AppPaths.AgentCredentialPath, forUpload: false, asSource: true) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result?.Session is not { } agent)
        { AgentStatusText.Text = "索引已上传。驻留程序尚未授权，远程原图和视频暂不可用。"; return; }
        var owner = gui.Connection.Session!;
        if (dialog.Result.ServiceId != gui.Connection.ServiceId || agent.AccountId != owner.AccountId || agent.SpaceId != owner.SpaceId)
            throw new InvalidOperationException("驻留程序必须授权给本次上传的同一账号和空间；索引已经上传，尚未启动媒体读取。");
        string publisher;
        using (var database = AppServices.Db.OpenIndependentConnection())
        using (var query = database.Command("SELECT publisher_id FROM sync_publishers WHERE device_grant_id=$grant AND space_id=$space"))
        {
            query.Parameters.AddWithValue("$grant", owner.GrantId); query.Parameters.AddWithValue("$space", owner.SpaceId);
            publisher = query.ExecuteScalar() as string ?? throw new InvalidOperationException("尚未建立来源同步关系。");
        }
        using var response = await gui.SpaceClient.PostAsJsonAsync("api/sync/v2/source-bindings",
            new SpaceSourceBindingRequest(publisher, agent.GrantId, scope.SourceIds), cancellationToken);
        response.EnsureSuccessStatusCode();
        var roots = AppServices.Db.GetSources().Where(source => scope.SourceIds.Contains(source.Id)).Select(source => source.Path).ToArray();
        if (!MediaAgentController.Start(dialog.Result.Origin, "", dialog.Result.SpkiPin, roots, AppPaths.AgentCredentialPath))
            throw new InvalidOperationException("索引和来源授权已保存，但驻留程序未能启动，请检查安装组件。");
        AgentStatusText.Text = "驻留程序已连接此空间，只向服务提供所选来源目录中的媒体。";
    }
}
