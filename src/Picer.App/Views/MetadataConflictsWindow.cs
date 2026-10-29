using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using Picer.App.Infrastructure;
using Picer.Core.Data;
using Picer.Core.Identity;

namespace Picer.App.Views;

public sealed class MetadataConflictsWindow : Window
{
    private readonly bool _remote;
    private readonly HttpClient? _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _token;
    private string? _epoch;
    private readonly ListBox _items = new() { DisplayMemberPath=nameof(ConflictRow.Label) };
    private readonly TextBlock _status = new() { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8) };
    private readonly Button _proposed=new() { Content="采用我的修改",Margin=new Thickness(0,0,12,0) };
    private readonly Button _current=new() { Content="保留当前版本",Margin=new Thickness(0,0,12,0) };
    private readonly Button _refresh=new() { Content="刷新列表" };
    private sealed record ConflictRow(MetadataEditConflict Conflict,string Title,bool Remote=false,string? ProposedDisplay=null,string? CurrentDisplay=null)
    {
        public string Label => $"{Title}\n{(Remote?"待处理修改":"我的修改")}：{ProposedDisplay??Value(Conflict.Proposed)}\n当前版本：{CurrentDisplay??Value(Conflict.Current)}";
        private static string Value(MetadataEdit edit) => edit.Kind.Contains("link",StringComparison.Ordinal)
            ? edit.Value=="1" ? "保留关联" : "移除关联"
            : edit.Kind=="space-setting" ? PreferenceValue(edit.Value)
            : edit.Kind=="person" && edit.Field.StartsWith("alias:",StringComparison.Ordinal) && edit.Value.Length==0 ? "移除此别名"
            : edit.Kind=="video-concept" ? edit.Value switch { "accepted"=>"确认", "rejected"=>"拒绝", "suggested"=>"建议", _=>edit.Value } : edit.Value;
        private static string PreferenceValue(string value)
        {
            var preference=System.Text.Json.JsonSerializer.Deserialize<ClusterPreference>(value)!;
            return $"{(preference.Automatic?"自动模式":"人工模式")}；人工阈值 {preference.ManualThreshold:F2}";
        }
    }
    public MetadataConflictsWindow(bool remote=false, HttpClient? client=null)
    {
        _client=client ?? AppServices.SpaceSynchronizationClient;
        _remote=remote || AppServices.ServiceMode && _client is not null; _token=_stop.Token;
        Title=_remote?"处理照片空间同步冲突":"处理同步冲突"; Width=700; Height=480; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        if (_remote) _proposed.Content="采用待处理修改";
        var layout=new DockPanel { Margin=new Thickness(16) };
        var help=new TextBlock { Text="同一字段在不同电脑上发生了修改，双方内容均已保留。选择一项后决定采用哪个版本；采纳修改会基于最新版本重新提交。此操作不会修改照片或视频原文件。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12) };
        DockPanel.SetDock(help,Dock.Top); layout.Children.Add(help);
        var bottom=new StackPanel(); DockPanel.SetDock(bottom,Dock.Bottom); bottom.Children.Add(_status);
        var actions=new WrapPanel();
        foreach (var button in new[]{_proposed,_current,_refresh}) actions.Children.Add(button);
        var catalogs=new Button { Content="分类删除冲突…",Margin=new Thickness(12,0,0,0) };
        catalogs.Click+=(_,_)=>new CatalogConflictsWindow(_remote,_client) { Owner=this }.ShowDialog();actions.Children.Add(catalogs);
        var history=new Button { Content="核对旧视频决定…",Margin=new Thickness(12,0,0,0) };
        history.Click+=(_,_)=>new VideoOwnershipReviewWindow { Owner=this }.ShowDialog(); actions.Children.Add(history);
        if (!_remote && _client is not null)
        {
            var space=new Button { Content="照片空间冲突…",Margin=new Thickness(12,0,0,0) };
            space.Click+=(_,_)=>new MetadataConflictsWindow(remote:true) { Owner=this }.ShowDialog(); actions.Children.Add(space);
        }
        bottom.Children.Add(actions); layout.Children.Add(bottom); layout.Children.Add(_items); Content=layout;
        AutomationProperties.SetAutomationId(_items,"MetadataConflicts");
        AutomationProperties.SetAutomationId(_proposed,"UseProposedMetadata");
        AutomationProperties.SetAutomationId(_current,"KeepCurrentMetadata");
        _proposed.Click+=async (_,_)=>await ResolveAsync(true); _current.Click+=async (_,_)=>await ResolveAsync(false);
        _refresh.Click+=async (_,_)=>await RefreshAsync(); Loaded+=async (_,_)=>await RefreshAsync();
        Closed+=(_,_)=> { _stop.Cancel(); _stop.Dispose(); };
    }
    private async Task RefreshAsync()
    {
        Busy(true);
        try
        {
            ConflictRow[] conflicts;
            if (_remote)
            {
                if (_client is null) { _status.Text="请先登录照片空间账号，再查看远程同步冲突。"; return; }
                using var response=await _client.PostAsJsonAsync("api/sync/v2/metadata-conflicts",new { },_token);
                response.EnsureSuccessStatusCode();
                var snapshot=await response.Content.ReadFromJsonAsync<MetadataConflictSnapshot>(DeviceAuthorization.Json,_token)
                    ?? throw new InvalidDataException("metadata_conflict_snapshot_missing");
                _epoch=snapshot.Epoch;
                conflicts=snapshot.Reviews.Select(review=>new ConflictRow(review.Conflict,review.Title,true,review.ProposedDisplay,review.CurrentDisplay)).ToArray();
            }
            else conflicts=await UiWork.ReadSnapshotAsync("Metadata.Conflicts", AppServices.Db, reader=>reader.GetMetadataEditConflicts()
                .Select(conflict=>new ConflictRow(conflict,AppServices.Db.DescribeMetadataEdit(conflict.Proposed),
                    ProposedDisplay:AppServices.Db.DescribeMetadataValue(conflict.Proposed),CurrentDisplay:AppServices.Db.DescribeMetadataValue(conflict.Current))).ToArray(),_token);
            _items.ItemsSource=conflicts;
            _status.Text=conflicts.Length==0 ? "当前没有待处理的人工字段冲突。" : $"待处理 {conflicts.Length} 项（每次最多显示 500 项）。";
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException exception) when (exception.StatusCode==System.Net.HttpStatusCode.Forbidden)
        { _status.Text="当前设备没有编辑权限，请使用有编辑权限的照片空间登录。"; }
        catch (Exception) { _status.Text="读取冲突失败，请确认登录和服务状态后刷新。"; }
        finally { Busy(false); }
    }
    private async Task ResolveAsync(bool proposed)
    {
        if (_items.SelectedItem is not ConflictRow row) { _status.Text="请先选择一项冲突。"; return; }
        Busy(true);
        try
        {
            var version=Math.Max(row.Conflict.Current.ServerVersion,row.Conflict.Current.BaseVersion);
            if (_remote)
            {
                if (_client is null) throw new InvalidOperationException("account_login_required");
                using var response=await _client.PostAsJsonAsync("api/sync/v2/metadata-conflicts/resolve",
                    new MetadataConflictResolution(row.Conflict.OperationId,proposed,version,row.Conflict.Current.OperationId,_epoch),_token);
                response.EnsureSuccessStatusCode();
            }
            else
            {
                await UiWork.RunDatabaseAsync(()=>AppServices.Db.ResolveMetadataEditConflict(row.Conflict.OperationId,proposed,
                    version,row.Conflict.Current.OperationId),_token);
                if (proposed) AppServices.QueueImmediateRemoteSync("人工解决同步冲突");
            }
            await RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _status.Text="内容可能已更新或暂时不可访问，请刷新列表后重新选择。"; }
        finally { Busy(false); }
    }
    private void Busy(bool busy) => _proposed.IsEnabled=_current.IsEnabled=_refresh.IsEnabled=_items.IsEnabled=!busy;
}
