using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Picer.App.Infrastructure;
using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Gateway;

namespace Picer.App.Views;

public sealed class CatalogConflictsWindow : Window
{
    private readonly bool _remote;
    private readonly HttpClient? _client;
    private readonly CancellationTokenSource _stop=new();
    private CancellationTokenSource? _previewStop;
    private readonly Image _preview=new() { Width=220,Height=180,Stretch=Stretch.Uniform,Margin=new Thickness(12,0,0,0) };
    private readonly TextBlock _previewStatus=new() { Text="选择一项查看预览",TextWrapping=TextWrapping.Wrap,Width=220,Margin=new Thickness(12) };
    private readonly ListBox _items=new() { DisplayMemberPath=nameof(Row.Label) };
    private readonly TextBlock _status=new() { TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8) };
    private readonly Button _keep=new() { Content="保留当前关联",Margin=new Thickness(0,0,12,0) };
    private readonly Button _delete=new() { Content="同步删除关联",Margin=new Thickness(0,0,12,0) };
    private readonly Button _refresh=new() { Content="刷新列表" };
    private sealed record Row(CatalogRetirementReview Review)
    {
        public string Label=>$"{Review.Title}\n{Review.MediaPath}\n{(Review.State=="scope_conflict"?"涉及未授权范围，需补充授权后才能删除":"另一端请求删除，但当前关联已有变化或缺少旧版本依据")}";
    }

    public CatalogConflictsWindow(bool remote=false,HttpClient? client=null)
    {
        _remote=remote;_client=client??AppServices.SpaceSynchronizationClient;
        Title=remote?"照片空间分类删除冲突":"分类删除冲突";Width=760;Height=500;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var layout=new DockPanel { Margin=new Thickness(16) };
        var help=new TextBlock { Text="选择一项照片或视频的分类关联进行处理。保留后会把此关联恢复到另一端；同步删除只移除此分类关联，不删除原文件。其他来源和未授权目录保持独立。另一台电脑离线时，决定会等待它重新连接。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12) };
        DockPanel.SetDock(help,Dock.Top);layout.Children.Add(help);
        var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);bottom.Children.Add(_status);
        var actions=new WrapPanel();foreach(var button in new[]{_keep,_delete,_refresh}) actions.Children.Add(button);
        bottom.Children.Add(actions);layout.Children.Add(bottom);
        var previewPanel=new StackPanel();previewPanel.Children.Add(_preview);previewPanel.Children.Add(_previewStatus);
        DockPanel.SetDock(previewPanel,Dock.Right);layout.Children.Add(previewPanel);layout.Children.Add(_items);Content=layout;
        AutomationProperties.SetAutomationId(_items,"CatalogRetirementConflicts");
        AutomationProperties.SetAutomationId(_keep,"KeepCatalogAssociation");AutomationProperties.SetAutomationId(_delete,"DeleteCatalogAssociation");
        _keep.Click+=async(_,_)=>await ResolveAsync(true);_delete.Click+=async(_,_)=>await ResolveAsync(false);
        _refresh.Click+=async(_,_)=>await RefreshAsync();Loaded+=async(_,_)=>await RefreshAsync();
        _items.SelectionChanged+=async(_,_)=>await PreviewAsync();
        Closed+=(_,_)=> { _previewStop?.Cancel();_stop.Cancel(); };
    }

    private async Task PreviewAsync()
    {
        _previewStop?.Cancel();_previewStop?.Dispose();
        _previewStop=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);var token=_previewStop.Token;
        _preview.Source=null;
        if(_items.SelectedItem is not Row row || row.Review.MediaId<=0) { _previewStatus.Text="暂无可用预览";return; }
        _previewStatus.Text="正在读取缩略图…";
        try
        {
            using var stream=await ReadPreviewAsync(row.Review,token);
            if(stream is null) { if(!token.IsCancellationRequested) _previewStatus.Text="缩略图暂不可用，仍可根据分类和路径审核。";return; }
            using var bytes=new MemoryStream();var buffer=new byte[32768];int read;
            while((read=await stream.ReadAsync(buffer,token))>0)
            {
                if(bytes.Length+read>8*1024*1024) throw new InvalidDataException("thumbnail_too_large");
                await bytes.WriteAsync(buffer.AsMemory(0,read),token);
            }
            token.ThrowIfCancellationRequested();bytes.Position=0;
            var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth=440;bitmap.StreamSource=bytes;bitmap.EndInit();bitmap.Freeze();
            _preview.Source=bitmap;_previewStatus.Text=row.Review.Anchor.MediaKind=="video"?"视频缩略图":"照片缩略图";
        }
        catch(OperationCanceledException) { }
        catch(Exception) { if(!token.IsCancellationRequested) _previewStatus.Text="缩略图暂不可用，请确认媒体读取权限或来源设备状态。"; }
    }

    private async Task<Stream?> ReadPreviewAsync(CatalogRetirementReview review,CancellationToken token)
    {
        if(_remote)
        {
            if(_client is null) return null;
            var path=review.Anchor.MediaKind=="video"?$"api/gateway/v1/video-thumbs/{review.MediaId}":$"api/gateway/v1/thumbs/{review.MediaId}";
            using var response=await _client.GetAsync(path,HttpCompletionOption.ResponseHeadersRead,token);
            response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength is >8*1024*1024) throw new InvalidDataException("thumbnail_too_large");
            await using var input=await response.Content.ReadAsStreamAsync(token);
            var output=new MemoryStream();
            try
            {
                var buffer=new byte[32768];int read;
                while((read=await input.ReadAsync(buffer,token))>0)
                {
                    if(output.Length+read>8*1024*1024) throw new InvalidDataException("thumbnail_too_large");
                    await output.WriteAsync(buffer.AsMemory(0,read),token);
                }
                output.Position=0;return output;
            }
            catch { output.Dispose();throw; }
        }
        return await UiWork.RunDatabaseAsync(()=>review.Anchor.MediaKind=="image"
            ?AppServices.Gateway.OpenThumbnailAsync(review.MediaId,token).GetAwaiter().GetResult()
            :AppServices.Gateway is IMediaAssetGateway media?media.OpenVideoThumbnailAsync(review.MediaId,cancellationToken:token).GetAwaiter().GetResult():null,token);
    }

    private async Task RefreshAsync()
    {
        Busy(true);
        try
        {
            IReadOnlyList<CatalogRetirementReview> reviews;
            if(_remote)
            {
                if(_client is null) throw new InvalidOperationException("account_login_required");
                using var response=await _client.PostAsJsonAsync("api/sync/v2/catalog-conflicts",new { },_stop.Token);
                response.EnsureSuccessStatusCode();
                reviews=await response.Content.ReadFromJsonAsync<CatalogRetirementReview[]>(DeviceAuthorization.Json,_stop.Token)
                    ??throw new InvalidDataException("catalog_reviews_missing");
            }
            else reviews=await UiWork.RunDatabaseAsync(()=>AppServices.Db.GetCatalogRetirementReviews(),_stop.Token);
            _items.ItemsSource=reviews.Select(review=>new Row(review)).ToArray();
            _status.Text=reviews.Count==0?"当前没有需要在此端处理的分类删除冲突。":$"待处理 {reviews.Count} 项，每次最多显示 200 项。";
        }
        catch(OperationCanceledException) { }
        catch(Exception) { _status.Text="读取失败，请确认登录、编辑权限和服务状态后刷新。"; }
        finally { Busy(false); }
    }

    private async Task ResolveAsync(bool keep)
    {
        if(_items.SelectedItem is not Row row) { _status.Text="请先选择一项分类关联。";return; }
        if(!keep && MessageBox.Show(this,$"确认移除以下关联？\n{row.Review.Title}\n{row.Review.MediaPath}\n原文件不会删除。","确认同步删除关联",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK) return;
        Busy(true);
        try
        {
            var decision=new CatalogRetirementDecision(row.Review,keep);
            if(_remote)
            {
                if(_client is null) throw new InvalidOperationException("account_login_required");
                using var response=await _client.PostAsJsonAsync("api/sync/v2/catalog-conflicts/resolve",decision,DeviceAuthorization.Json,_stop.Token);
                response.EnsureSuccessStatusCode();
            }
            else
            {
                await UiWork.RunDatabaseAsync(()=>AppServices.Db.ResolveCatalogRetirementReview(decision,
                    new SpaceUploadScope(AppServices.Db.GetSources().Select(source=>source.Id).ToArray())),_stop.Token);
                AppServices.QueueImmediateRemoteSync("人工处理分类删除冲突");
            }
            await RefreshAsync();
        }
        catch(OperationCanceledException) { }
        catch(Exception) { _status.Text="内容、授权或同步状态已经变化，请刷新后重新审核。决定未强制覆盖新内容。"; }
        finally { Busy(false); }
    }

    private void Busy(bool busy)=>_items.IsEnabled=_keep.IsEnabled=_delete.IsEnabled=_refresh.IsEnabled=!busy;
}
