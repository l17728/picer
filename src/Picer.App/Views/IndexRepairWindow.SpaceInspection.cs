using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Automation;
using Picer.Core.Identity;

namespace Picer.App.Views;

public sealed partial class IndexRepairWindow
{
    private readonly ListBox _spaceInspectionTargets = new() { Height=110,Margin=new Thickness(0,4,0,8) };
    private readonly TextBlock _spaceInspectionStatus = new() { TextWrapping=TextWrapping.Wrap };
    private readonly DispatcherTimer _spaceInspectionTimer = new() { Interval=TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource _spaceInspectionReadCancellation = new();
    private string? _spaceInspectionJob;
    private bool _spaceInspectionActive;
    private bool _spaceInspectionReading;
    private bool _spaceInspectionRequestPending;
    private bool _spaceInspectionClosed;

    private string? SpaceInspectionCheckpoint => AppServices.SpaceSynchronizationClient?.BaseAddress is { } address
        ? Path.Combine(AppPaths.DataRoot,"space-inspection-"+DeviceAuthorization.Digest(address.AbsoluteUri)[..16]+".txt") : null;

    private void InitializeSpaceInspectionUi(DockPanel layout)
    {
        if (AppServices.SpaceSynchronizationClient is null) return;
        Height=630;
        AutomationProperties.SetAutomationId(_spaceInspectionStatus,"SpaceInspectionStatus");
        AutomationProperties.SetAutomationId(_spaceInspectionTargets,"SpaceInspectionTargets");
        var panel = new StackPanel(); panel.Children.Add(_spaceInspectionStatus); panel.Children.Add(_spaceInspectionTargets);
        DockPanel.SetDock(panel,Dock.Top); layout.Children.Insert(1,panel);
        _spaceInspectionTimer.Tick += async (_,_) => await RefreshSpaceInspectionAsync();
        Loaded += async (_,_) =>
        {
            try
            {
                if (SpaceInspectionCheckpoint is { } path && File.Exists(path))
                {
                    var job = (await File.ReadAllTextAsync(path,_spaceInspectionReadCancellation.Token)).Trim();
                    if (Guid.TryParseExact(job,"N",out _))
                    { _spaceInspectionJob=job; _spaceInspectionRequestPending=true; await RefreshSpaceInspectionAsync(); }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { _spaceInspectionStatus.Text="上次巡检记录无法读取，可重新发起。"; }
        };
        Closing += (_,_) => { _spaceInspectionClosed=true; _spaceInspectionTimer.Stop(); _spaceInspectionReadCancellation.Cancel(); };
        Closed += (_,_) => _spaceInspectionReadCancellation.Dispose();
    }

    private async Task StartSpaceInspectionAsync(CancellationToken token)
    {
        var client=AppServices.SpaceSynchronizationClient;
        if (client is null) return;
        if (_spaceInspectionActive && !_spaceInspectionRequestPending) { await RefreshSpaceInspectionAsync(); return; }
        var jobId=_spaceInspectionRequestPending && _spaceInspectionJob is { } existing ? existing : Guid.NewGuid().ToString("N");
        _spaceInspectionJob=jobId;
        _spaceInspectionActive=true; _spaceInspectionRequestPending=true; _spaceInspectionTimer.Start();
        if (SpaceInspectionCheckpoint is { } path)
            await File.WriteAllTextAsync(path,jobId,token);
        var job=await new SpaceInspectionClient(client).CreateAsync(jobId,token);
        ShowSpaceInspection(job);
        await RefreshSpaceInspectionAsync();
    }

    private async Task RefreshSpaceInspectionAsync()
    {
        var client=AppServices.SpaceSynchronizationClient;
        if (_spaceInspectionClosed || client is null || _spaceInspectionJob is null || _spaceInspectionReading) return;
        _spaceInspectionReading=true;
        try
        {
            var job=await new SpaceInspectionClient(client).ReadAsync(_spaceInspectionJob,_spaceInspectionReadCancellation.Token);
            ShowSpaceInspection(job);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        { if (!_spaceInspectionClosed) { _spaceInspectionStatus.Text="来源状态暂时无法刷新，任务仍保留在照片空间，稍后重试。"; _spaceInspectionTimer.Start(); } }
        finally { _spaceInspectionReading=false; }
    }

    private void ShowSpaceInspection(SpaceInspectionJob job)
    {
        if (_spaceInspectionClosed) return;
        if (job.JobId != _spaceInspectionJob || job.Targets is null || job.Targets.Any(target=>target is null || target.DeviceId is null))
            throw new InvalidDataException("巡检任务身份不一致。");
        _spaceInspectionRequestPending=false;
        _spaceInspectionActive=job.State=="active";
        var completed=job.Targets.Count(target=>target.State=="completed");
        _spaceInspectionStatus.Text=job.State switch
        {
            "completed"=>$"空间巡检完成：{completed}/{job.Targets.Count} 个来源已确认。",
            "pending_review"=>$"来源同步已完成，仍有 {job.PendingMetadataConflicts} 项空间人工字段冲突、{job.PendingOwnershipReviews} 项旧视频决定待审核。点击“处理同步冲突…”处理；本地模式下可进入“照片空间冲突…”，旧视频决定在“核对旧视频决定…”中处理。",
            "cancelled"=>"空间巡检已取消；已完成的同步保留。",
            "partially_failed"=>$"空间巡检未全部完成：{completed}/{job.Targets.Count} 个来源已确认，请查看失败或撤权来源。",
            _=>$"空间巡检进行中：{completed}/{job.Targets.Count} 个来源已确认；离线来源等待上线。"
        };
        if (job.PendingOwnershipReviews > 0 && job.State != "pending_review")
            _spaceInspectionStatus.Text += $" 另有 {job.PendingOwnershipReviews} 项旧视频决定待审核。";
        if (job.PendingMetadataConflicts > 0 && job.State != "pending_review")
            _spaceInspectionStatus.Text += $" 另有 {job.PendingMetadataConflicts} 项空间人工字段冲突待处理。";
        _spaceInspectionTargets.ItemsSource=job.Targets.Select(target=>$"{target.DeviceName??("来源设备 "+target.DeviceId[..Math.Min(8,target.DeviceId.Length)])} · "+(target.State switch
        {
            "completed"=>"已完成", "offline"=>"设备离线，等待上线", "queued"=>"等待通知",
            "notified"=>"已通知，等待执行", "running"=>"正在巡检与同步", "revoked"=>"已撤权，无法继续",
            "unbound"=>"来源未绑定，需在来源电脑登录并授权目录",
            "failed" when target.ErrorCode=="pending_identity_adoptions"=>"认领仍暂缓，请在该来源核对授权范围后重新发起",
            "failed" when target.ErrorCode=="source_database_integrity_failed"=>"数据库巡检未通过，请在该来源查看诊断中心",
            "failed" when target.ErrorCode=="source_paths_unavailable"=>"来源目录或文件不可访问，请检查磁盘、权限或网络后重新发起",
            "failed" when target.ErrorCode=="metadata_conflicts_pending"=>"有人工修改冲突，请在该来源点击“处理同步冲突”后重新巡检",
            "cancelled"=>"已取消", _=>"未完成，请检查该来源的同步状态后重新发起"
        })+(DateTime.TryParse(target.LastSuccessUtc,out var success) ? " · 最近成功 "+success.ToLocalTime().ToString("MM-dd HH:mm") : " · 尚无成功回执")).ToArray();
        _cancel.IsEnabled=_cancellation is not null || _spaceInspectionActive;
        if (_spaceInspectionActive) _spaceInspectionTimer.Start(); else _spaceInspectionTimer.Stop();
    }

    private async Task CancelSpaceInspectionAsync()
    {
        var client=AppServices.SpaceSynchronizationClient;
        if (client is null || _spaceInspectionJob is null || !_spaceInspectionActive) return;
        try
        {
            await new SpaceInspectionClient(client).CancelAsync(_spaceInspectionJob,_spaceInspectionReadCancellation.Token);
            await RefreshSpaceInspectionAsync();
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { _spaceInspectionStatus.Text="尚未确认空间任务取消；请刷新状态后重试。"; }
    }
}
