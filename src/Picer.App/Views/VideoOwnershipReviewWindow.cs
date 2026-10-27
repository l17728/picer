using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Picer.App.Infrastructure;
using Picer.Core.Data;
using Picer.Core.Identity;

namespace Picer.App.Views;

public sealed class VideoOwnershipReviewWindow : Window
{
    private readonly HttpClient? _client=AppServices.SpaceSynchronizationClient;
    private readonly CancellationTokenSource _stop=new();
    private readonly CancellationToken _token;
    private readonly ListBox _reviews=new() { DisplayMemberPath=nameof(ReviewRow.Label),MinHeight=110 };
    private readonly ListBox _sources=new() { DisplayMemberPath=nameof(SourceRow.Label),SelectionMode=SelectionMode.Multiple,MinHeight=110 };
    private readonly TextBlock _status=new() { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8) };
    private readonly Button _apply=new() { Content="应用到所选来源",Margin=new Thickness(0,0,12,0) };
    private readonly Button _discard=new() { Content="忽略该旧决定",Margin=new Thickness(0,0,12,0) };
    private readonly Button _refresh=new() { Content="刷新" };
    private sealed record ReviewRow(VideoOwnershipReview Review)
    { public string Label => Review.Description+" · 旧决定："+Value(Review.Value); }
    private sealed record SourceRow(VideoOwnershipCandidate Candidate)
    {
        public string Label
        {
            get
            {
                var path=Candidate.Path;
                var published=PublishedMediaPath.TryUnqualify(path,out var publisher,out var original);
                return (Candidate.SourceName??(published?"来源 "+publisher[..8]:"当前图库"))+" · "+(published?original:path)+
                    " · 当前："+(Candidate.CurrentValue is null?"尚无人工决定":Value(Candidate.CurrentValue));
            }
        }
    }
    private static string Value(string value) => value switch { "1"=>"保留关联","0"=>"移除关联","accepted"=>"确认","rejected"=>"拒绝","suggested"=>"建议",_=>value };
    public VideoOwnershipReviewWindow()
    {
        _token=_stop.Token;
        Title="核对旧视频决定"; Width=850; Height=550; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var layout=new DockPanel { Margin=new Thickness(16) };
        var help=new TextBlock { Text="这些旧记录已丢失来源信息，尚未自动分配。先选择一条旧决定，再选择它应属于的来源；可以多选。只有选中的来源会应用该决定。忽略只结束本条旧记录的审核，不删除视频或其他现有决定。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12) };
        DockPanel.SetDock(help,Dock.Top); layout.Children.Add(help);
        var bottom=new StackPanel(); DockPanel.SetDock(bottom,Dock.Bottom); bottom.Children.Add(_status);
        var actions=new StackPanel { Orientation=Orientation.Horizontal };
        foreach (var button in new[]{_apply,_discard,_refresh}) actions.Children.Add(button);
        bottom.Children.Add(actions); layout.Children.Add(bottom);
        var center=new Grid(); center.RowDefinitions.Add(new RowDefinition()); center.RowDefinitions.Add(new RowDefinition());
        center.Children.Add(_reviews); Grid.SetRow(_sources,1); center.Children.Add(_sources); layout.Children.Add(center); Content=layout;
        AutomationProperties.SetAutomationId(_reviews,"VideoOwnershipReviews"); AutomationProperties.SetAutomationId(_sources,"VideoOwnershipSources");
        AutomationProperties.SetAutomationId(_apply,"ApplyVideoOwnership"); AutomationProperties.SetAutomationId(_discard,"DiscardVideoOwnership");
        _reviews.SelectionChanged+=(_,_)=>_sources.ItemsSource=(_reviews.SelectedItem as ReviewRow)?.Review.Candidates.Select(candidate=>new SourceRow(candidate)).ToArray();
        _apply.Click+=async (_,_)=>await ResolveAsync(false); _discard.Click+=async (_,_)=>await ResolveAsync(true);
        _refresh.Click+=async (_,_)=>await RefreshAsync(); Loaded+=async (_,_)=>await RefreshAsync();
        Closed+=(_,_)=> { _stop.Cancel(); _stop.Dispose(); };
    }
    private async Task RefreshAsync()
    {
        Busy(true);
        try
        {
            IReadOnlyList<VideoOwnershipReview> rows;
            if (_client is null)
            {
                if (AppServices.ServiceMode) throw new InvalidOperationException("请先登录照片空间账号，再审核远程历史决定。");
                rows=await UiWork.RunDatabaseAsync(()=>AppServices.Db.GetVideoOwnershipReviews(),_token);
            }
            else
            {
                using var response=await _client.PostAsJsonAsync("api/sync/v2/video-ownership-reviews",new { },_token);
                response.EnsureSuccessStatusCode();
                rows=await response.Content.ReadFromJsonAsync<VideoOwnershipReview[]>(DeviceAuthorization.Json,_token)??[];
            }
            _reviews.ItemsSource=rows.Select(row=>new ReviewRow(row)).ToArray(); _sources.ItemsSource=null;
            _status.Text=rows.Count==0?"没有待核对的旧视频决定。":$"待核对 {rows.Count} 条（每次最多显示 100 条）。请选择记录和来源。";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _status.Text="读取未完成，请确认已登录照片空间、服务可用后刷新；本地模式可离线审核当前图库。"; }
        finally { Busy(false); }
    }
    private async Task ResolveAsync(bool discard)
    {
        if (_reviews.SelectedItem is not ReviewRow selected) { _status.Text="请先选择一条旧决定。"; return; }
        VideoOwnershipChoice[] choices=discard?[]:_sources.SelectedItems.Cast<SourceRow>().Select(row=>new VideoOwnershipChoice(row.Candidate.VideoId,row.Candidate.Version,row.Candidate.CurrentOperationId)).ToArray();
        if (!discard && choices.Length==0) { _status.Text="请至少选择一个来源。"; return; }
        Busy(true);
        try
        {
            var request=new VideoOwnershipResolution(selected.Review.OperationId,choices,discard,selected.Review.Epoch);
            if (_client is null)
            {
                await UiWork.RunDatabaseAsync(()=>AppServices.Db.ResolveVideoOwnershipReview(request),_token);
                AppServices.QueueImmediateRemoteSync("视频历史归属审核");
            }
            else
            {
                using var response=await _client.PostAsJsonAsync("api/sync/v2/video-ownership-reviews/resolve",request,_token);
                response.EnsureSuccessStatusCode();
            }
            await RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _status.Text="提交未完成，或当前决定已经变化。请刷新并重新核对；已提交的选择不会因重试重复应用。"; }
        finally { Busy(false); }
    }
    private void Busy(bool busy) => _apply.IsEnabled=_discard.IsEnabled=_refresh.IsEnabled=_reviews.IsEnabled=_sources.IsEnabled=!busy;
}
