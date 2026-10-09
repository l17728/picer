using System.IO;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Picer.App.Infrastructure;
using Picer.Core.Indexing;

namespace Picer.App.Views;

public sealed class IndexRepairWindow : Window
{
    private readonly ListBox _roots = new() { MinHeight = 130 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button _start = new() { Content = "开始修复索引", Padding = new Thickness(12, 5, 12, 5) };
    private readonly Button _add = new() { Content = "添加根目录…", Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _remove = new() { Content = "移除选中目录", Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _cancel = new() { Content = "取消", IsEnabled = false, Margin = new Thickness(12, 0, 0, 0) };
    private CancellationTokenSource? _cancellation;

    public IndexRepairWindow()
    {
        Title = "修复移动后的照片和视频索引"; Width = 720; Height = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(16) };
        var help = new TextBlock { Text = "添加照片或视频移动后的根目录，程序扫描子目录，以 SHA-256 恢复关联并保留描述和人物标记。未找到的原位置标注“已删除或移动”，原文件不会被修改。旧视频若未记录内容哈希且原文件已丢失，将保留为未找到，不按文件名猜测。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
        DockPanel.SetDock(help, Dock.Top); layout.Children.Add(help);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { _add, _remove, _start, _cancel }) buttons.Children.Add(button);
        bottom.Children.Add(buttons); layout.Children.Add(bottom); layout.Children.Add(_roots); Content = layout;
        _add.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "选择照片和视频的新位置（可多选，也可分次添加）", Multiselect = true };
            if (picker.ShowDialog(this) == true)
                foreach (var path in picker.FolderNames)
                    if (!_roots.Items.Cast<string>().Contains(path, StringComparer.OrdinalIgnoreCase)) _roots.Items.Add(path);
        };
        _remove.Click += (_, _) => { if (_roots.SelectedItem is { } selected) _roots.Items.Remove(selected); };
        _start.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        Closing += (_, _) => _cancellation?.Cancel();
    }

    private async Task RunAsync()
    {
        if (_cancellation is not null || _roots.Items.Count == 0) return;
        _cancellation = new(); var token = _cancellation.Token;
        _start.IsEnabled = _add.IsEnabled = _remove.IsEnabled = false; _cancel.IsEnabled = true;
        var restored = 0; var added = 0; var missing = 0; var failed = 0;
        try
        {
            var roots = _roots.Items.Cast<string>().ToArray();
            _status.Text = "读取索引并检查原位置…";
            if (AppServices.ServiceMode) await AppServices.SyncRemoteMirrorAsync(token, requireMetadataSync: true, refreshMedia: true);
            var images = await UiWork.RunDatabaseAsync(() => AppServices.Db.GetImages(includeMissing: true), token);
            var videos = await UiWork.RunDatabaseAsync(() => new Picer.Core.Video.VideoRepository(AppServices.Db).GetAll(), token);
            var progress = new Progress<string>(message => _status.Text = message);
            var plan = await Task.Run(() => IndexRepairService.BuildAsync(images, roots, progress, token, videos), token);
            using var mutation = await AppServices.AcquireIndexMutationAsync(token);
            failed = plan.Failures.Count;
            var backend = AppPaths.LoadIndexBackend();
            using var client = AppServices.ServiceMode ? AppServices.CreatePinnedServiceClient(backend, TimeSpan.FromMinutes(10)) : null;
            if (client is not null && plan.Operations.Count > 0)
            {
                // Root selection is the source host's authorization. Persist it
                // locally; the server cannot expand the agent's allowed roots.
                MediaAgentController.AuthorizeRepairRoots(roots);
                var sources = await UiWork.RunDatabaseAsync(AppServices.Db.GetSources, token);
                var settings = MediaAgentController.Load();
                var ip = string.IsNullOrWhiteSpace(settings.ServiceIp) ? new Uri(backend.BaseUrl!).Host : settings.ServiceIp;
                if (!await Task.Run(() => MediaAgentController.Start(ip, MediaAgentController.ResolveToken(), MediaAgentController.ResolveSpkiPin(),
                        sources.Select(source => IndexRepairService.LocalPath(source.Path)).Concat(roots)), token))
                    throw new InvalidOperationException("无法启动源文件代理，尚未修改索引。");
            }
            var done = 0;
            foreach (var operation in plan.Operations)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    IndexRepairResult result;
                    if (client is not null)
                    {
                        using var response = await client.PostAsJsonAsync(new Uri(new Uri(backend.BaseUrl!), "/api/service/v1/index-repair"), operation, token);
                        response.EnsureSuccessStatusCode();
                        result = await response.Content.ReadFromJsonAsync<IndexRepairResult>(cancellationToken: token) ?? throw new InvalidDataException("修复服务未返回结果");
                    }
                    else
                    {
                        var evidence = await IndexRepairService.VerifyAsync(operation, token);
                        result = await UiWork.RunDatabaseAsync(() => IndexRepairService.Apply(AppServices.Db, operation, evidence), token);
                    }
                    switch (result.Status)
                    {
                        case "restored": restored++; break;
                        case "added": added++; break;
                        case "missing": missing++; break;
                        case "failed": failed++; break;
                    }
                }
                catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or IOException or InvalidOperationException)
                { failed++; }
                _status.Text = $"修复 {++done}/{plan.Operations.Count}：恢复 {restored}，新增位置引用 {added}，已删除或移动 {missing}，失败 {failed}";
            }
            await AppServices.RefreshMirrorAfterIndexMutationAsync(token);
            _status.Text = $"修复完成：恢复 {restored}，新增位置引用 {added}，已删除或移动 {missing}，失败 {failed}。失败项保留，可再次修复。";
        }
        catch (OperationCanceledException) { _status.Text = $"已取消，已完成的修复保留：恢复 {restored}，新增引用 {added}，未找到 {missing}。"; }
        catch (Exception exception) { _status.Text = "修复未完成：" + exception.Message; }
        finally
        {
            _cancellation.Dispose(); _cancellation = null;
            _start.IsEnabled = _add.IsEnabled = _remove.IsEnabled = true; _cancel.IsEnabled = false;
        }
    }
}
