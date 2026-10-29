using System.IO;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Microsoft.Win32;
using Picer.App.Infrastructure;
using Picer.Core.Indexing;

namespace Picer.App.Views;

public sealed partial class IndexRepairWindow : Window
{
    private readonly ListBox _roots = new() { MinHeight = 130 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button _inspectSync = new() { Content = "巡检并同步用户空间", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _start = new() { Content = "开始修复索引", Padding = new Thickness(12, 5, 12, 5) };
    private readonly Button _add = new() { Content = "添加根目录…", Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _remove = new() { Content = "移除选中目录", Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _cancel = new() { Content = "取消", IsEnabled = false, Margin = new Thickness(12, 0, 0, 0) };
    private CancellationTokenSource? _cancellation;

    public IndexRepairWindow()
    {
        Title = "索引巡检、同步与修复"; Width = 760; Height = 470; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(16) };
        var help = new TextBlock { Text = "空间巡检按各来源保存的授权范围执行，并显示逐来源回执；离线来源等待上线，不会替其他电脑增加目录或扩大权限。“开始修复索引”用于照片/视频移动后的重新关联：添加新位置根目录，程序扫描子目录，以 SHA-256 恢复关联并保留描述和人物标记。未找到的原位置标注“已删除或移动”。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
        DockPanel.SetDock(help, Dock.Top); layout.Children.Add(help);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(_status);
        var conflicts = new Button { Content = "处理同步冲突…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,0,0,8) };
        AutomationProperties.SetAutomationId(conflicts,"OpenMetadataConflicts");
        conflicts.Click += async (_,_) =>
        {
            new MetadataConflictsWindow { Owner = this }.ShowDialog();
            await RefreshSpaceInspectionAsync();
        };
        bottom.Children.Add(conflicts);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { _inspectSync, _add, _remove, _start, _cancel }) buttons.Children.Add(button);
        bottom.Children.Add(buttons); layout.Children.Add(bottom); layout.Children.Add(_roots); Content = layout;
        InitializeSpaceInspectionUi(layout);
        AutomationProperties.SetAutomationId(_inspectSync,"InspectSpaceIndex");
        AutomationProperties.SetAutomationId(_status,"IndexInspectionStatus");
        AutomationProperties.SetAutomationId(_cancel,"CancelIndexWork");
        if (AppServices.SpaceSynchronizationClient is null) _inspectSync.Content="巡检当前索引";
        _add.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "选择照片和视频的新位置（可多选，也可分次添加）", Multiselect = true };
            if (picker.ShowDialog(this) == true)
                foreach (var path in picker.FolderNames)
                    if (!_roots.Items.Cast<string>().Contains(path, StringComparer.OrdinalIgnoreCase)) _roots.Items.Add(path);
        };
        _remove.Click += (_, _) => { if (_roots.SelectedItem is { } selected) _roots.Items.Remove(selected); };
        _inspectSync.Click += async (_, _) => await InspectAndSyncAsync();
        _start.Click += async (_, _) => await RunAsync();
        _cancel.Click += async (_, _) => { _cancellation?.Cancel(); await CancelSpaceInspectionAsync(); };
        Closing += (_, _) => _cancellation?.Cancel();
    }

    private async Task InspectAndSyncAsync()
    {
        if (_cancellation is not null) return;
        _cancellation = new();
        var token = _cancellation.Token;
        SetBusy(true);
        try
        {
            _status.Text = "正在同步用户空间索引…";
            if (AppServices.ServiceMode)
                await AppServices.SyncRemoteMirrorAsync(token, requireMetadataSync: true);
            var newlyMissing = 0;
            if (!AppServices.ServiceMode && AppServices.SpaceSynchronizationClient is null)
            {
                _status.Text = "正在检查本机授权来源中的索引文件…";
                var inspection=await AppServices.Index.InspectReferencesAsync(cancellationToken:token);
                newlyMissing=checked((int)(inspection.MissingPhotos+inspection.MissingVideos));
                if (!inspection.Complete) throw new IOException("部分来源不可访问；其原有状态已保留，请检查磁盘、权限或网络连接。");
            }
            var pushed = await AppServices.SyncLocalChangesToRemoteAsync(token);
            // A remote mirror pull after publishing ensures the inspection uses
            // the latest committed cut, including changes from other clients.
            if (AppServices.ServiceMode)
                await AppServices.SyncRemoteMirrorAsync(token, requireMetadataSync: true);

            var integrity = await UiWork.ReadSnapshotAsync("Inspection.Integrity", AppServices.Db, reader =>
            {
                using var check = reader.Command("PRAGMA quick_check");
                return Convert.ToString(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
            }, token);
            var counts = await UiWork.ReadSnapshotAsync("Inspection.Counts", AppServices.Db, reader =>
            {
                using var query = reader.Command("SELECT (SELECT COUNT(*) FROM images),(SELECT COUNT(*) FROM images WHERE status<>'active'),(SELECT COUNT(*) FROM videos),(SELECT COUNT(*) FROM videos WHERE status<>'active')");
                using var row = query.ExecuteReader(); row.Read();
                return (Images: row.GetInt64(0), MissingImages: row.GetInt64(1), Videos: row.GetInt64(2), MissingVideos: row.GetInt64(3));
            }, token);
            var syncSummary = pushed is null
                ? AppServices.LocalSpaceSyncConfigured ? "已按用户选择的来源范围执行照片空间增量发布" : "当前没有配置可写的同步服务"
                : $"本次提交 {pushed.Images} 张照片、{pushed.Videos} 个视频的变更";
            var fileCheck = AppServices.SpaceSynchronizationClient is not null ? "各来源按已授权范围检查文件引用" : $"本机核查发现 {newlyMissing} 个新缺失文件";
            await StartSpaceInspectionAsync(token);
            var spaceSummary=AppServices.SpaceSynchronizationClient is null ? "当前未连接账号空间，本次仅巡检当前索引。" : "空间其他来源结果见来源状态列表；离线来源继续等待。";
            _status.Text = AppServices.SpaceSynchronizationClient is null
                ? $"当前索引巡检完成；{syncSummary}。{fileCheck}。数据库完整性：{integrity}。照片 {counts.Images}（缺失/移动 {counts.MissingImages}），视频 {counts.Videos}（缺失/移动 {counts.MissingVideos}）。{spaceSummary}缺失项不会自动删除。"
                : $"空间巡检已提交；本机数据库完整性：{integrity}。当前缓存有照片 {counts.Images}、视频 {counts.Videos}。{fileCheck}；请以逐来源回执为准。{spaceSummary}缺失项不会自动删除。";
        }
        catch (OperationCanceledException) { _status.Text = "巡检已取消；已提交的同步页仍保留，下一次可继续。"; }
        catch (Exception exception) { _status.Text = "巡检或同步未完成；已完成的页面保留，可重试：" + exception.Message; }
        finally
        {
            _cancellation.Dispose(); _cancellation = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _inspectSync.IsEnabled = _add.IsEnabled = _remove.IsEnabled = _start.IsEnabled = !busy;
        _cancel.IsEnabled = busy || _spaceInspectionActive;
    }

    private async Task RunAsync()
    {
        if (_cancellation is not null || _roots.Items.Count == 0) return;
        _cancellation = new(); var token = _cancellation.Token;
        SetBusy(true);
        var restored = 0; var added = 0; var missing = 0; var failed = 0;
        try
        {
            var roots = _roots.Items.Cast<string>().ToArray();
            _status.Text = "读取索引并检查原位置…";
            if (AppServices.ServiceMode) await AppServices.SyncRemoteMirrorAsync(token, requireMetadataSync: true, refreshMedia: true);
            var snapshot = await UiWork.ReadSnapshotAsync("Repair.Input", AppServices.Db, reader => (Images: reader.GetImages(includeMissing: true), Videos: new Picer.Core.Video.VideoRepository(reader).GetAll()), token);
            var images = snapshot.Images;
            var videos = snapshot.Videos;
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
                var sources = await UiWork.ReadSnapshotAsync("Repair.Sources", AppServices.Db, reader => reader.GetSources(), token);
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
            SetBusy(false);
        }
    }
}
