using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Picer.App.Infrastructure;
using Picer.App.ViewModels;
using Picer.Core.Gateway;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.App.Views;

public sealed class DirectoryMediaDescriptionWindow : Window
{
    private readonly TreeView _tree = new() { MinHeight = 180 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _syncStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _authorization = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _start = new() { Content = "开始描述照片和视频", IsEnabled = false, Padding = new Thickness(12, 6, 12, 6), ToolTip = "请先在目录树中选择一个目录" };
    private readonly Button _cancel = new() { Content = "取消任务", IsEnabled = false, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Button _retry = new() { Content = "仅重试失败项", IsEnabled = false, Margin = new Thickness(12, 0, 0, 0) };
    private readonly ObservableCollection<MediaDescriptionFailure> _failures = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ComboBox _concurrency = new() { ItemsSource = new[] { 1, 2, 3, 4 }, SelectedItem = 2, Width = 70, Margin = new Thickness(8, 0, 0, 0) };
    private CancellationTokenSource? _cancellation;
    private DirectoryMediaDescriptionPlan? _plan;

    public DirectoryMediaDescriptionWindow(bool loadOnShow = true)
    {
        Title = "描述目录中的照片和视频";
        Width = 720; Height = 620; MinWidth = 560; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(16) };
        var header = new TextBlock { Text = "描述所选目录及其所有子目录中已入库的照片和视频，自动跳过已有有效描述。新文件请先更新索引。", TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(_authorization); bottom.Children.Add(_status);
        bottom.Children.Add(_syncStatus);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_syncStatus, "DirectoryDescriptionSyncStatus");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_status, System.Windows.Automation.AutomationLiveSetting.Polite);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_status, "DirectoryDescriptionStatus");
        var concurrencyPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 8) };
        concurrencyPanel.Children.Add(new TextBlock { Text = "本地照片/视频并发数（云端为 1）：", VerticalAlignment = VerticalAlignment.Center });
        System.Windows.Automation.AutomationProperties.SetName(_concurrency, "本地照片和视频并发数");
        concurrencyPanel.Children.Add(_concurrency); bottom.Children.Add(concurrencyPanel);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        System.Windows.Automation.AutomationProperties.SetName(_start, "开始描述照片和视频");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_start, "StartDirectoryMediaDescription");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_cancel, "CancelDirectoryMediaDescription");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_retry, "RetryFailedDirectoryDescriptions");
        actions.Children.Add(_start); actions.Children.Add(_cancel); actions.Children.Add(_retry); bottom.Children.Add(actions);
        var failures = new ListBox { ItemsSource = _failures, MaxHeight = 110 };
        System.Windows.Automation.AutomationProperties.SetName(failures, "目录描述失败清单");
        bottom.Children.Add(new Expander { Header = "失败清单（取消不计为失败）", Content = failures });
        layout.Children.Add(bottom); layout.Children.Add(_tree); Content = layout;
        System.Windows.Automation.AutomationProperties.SetName(_tree, "选择照片和视频所在目录");
        _tree.SelectedItemChanged += async (_, args) => await PreviewAsync(args.NewValue);
        _start.Click += async (_, _) => await StartAsync();
        _retry.Click += async (_, _) => await StartAsync(retryOnly: true);
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        Closing += (_, e) => { if (_cancellation is not null) { _cancellation.Cancel(); e.Cancel = true; _status.Text = "正在取消，已完成描述将保留；结束后可关闭窗口。"; } };
        Closed += (_, _) => _lifetime.Cancel();
        if (loadOnShow) Loaded += async (_, _) =>
        {
            try
            {
                var paths = await Task.Run(() =>
                {
                    using var reader = AppServices.Db.OpenIndependentConnection();
                    return reader.GetDirectoryTreePaths(_lifetime.Token);
                }, _lifetime.Token);
                var roots = await Task.Run(() => DirectoryTreeModel.Build(paths, _lifetime.Token), _lifetime.Token);
                PopulateDirectoryModels(roots);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception) { _status.Text = "目录加载失败：" + exception.Message; }
        };
    }

    internal TreeView DirectoryTree => _tree;
    internal Button StartButton => _start;
    internal string StatusMessage => _status.Text;

    private async Task RefreshSyncStatusAsync(CancellationToken token)
    {
        if (!AppServices.Db.EnableDescriptionSync) return;
        var counts = await UiWork.ReadSnapshotAsync("Descriptions.SyncCounts", AppServices.Db, reader => reader.GetDescriptionSyncCounts(), token);
        await Dispatcher.InvokeAsync(() => _syncStatus.Text =
            $"本机结果已持久保存；当前照片空间待上传 {counts.Pending} 个描述版本，服务端已确认、待回读核对 {counts.AwaitingSnapshot} 个版本。成功计数不代表全部已同步。",
            System.Windows.Threading.DispatcherPriority.Background, token);
    }

    internal void PopulateDirectories(IEnumerable<string> paths) => PopulateDirectoryModels(DirectoryTreeModel.Build(paths));

    private void PopulateDirectoryModels(IReadOnlyList<DirectoryTreeModel> roots)
    {
        _tree.Items.Clear();
        foreach (var root in roots) _tree.Items.Add(CreateDirectoryNode(root, 0));
        _start.IsEnabled = roots.Count > 0;
        _tree.IsEnabled = roots.Count > 0;
        _status.Text = roots.Count > 0
            ? "请先单击目录树中的目录名称选择范围；选定后点击“开始描述照片和视频”。重新运行会跳过已有描述。"
            : "当前索引没有可选择的照片/视频目录。请先添加并完成目录索引；远程模式请等待照片空间同步后重新打开此窗口。";
    }

    private static TreeViewItem CreateDirectoryNode(DirectoryTreeModel model, int eagerDepth)
    {
        var node = new TreeViewItem { Header = model.Name, Tag = model.Path, IsExpanded = eagerDepth == 2 };
        if (eagerDepth > 0)
            foreach (var child in model.Children) node.Items.Add(CreateDirectoryNode(child, eagerDepth - 1));
        else if (model.Children.Count > 0)
        {
            node.Items.Add(new TreeViewItem());
            node.Expanded += async (_, args) =>
            {
                if (!ReferenceEquals(args.OriginalSource, node) || node.Items.Count != 1 || node.Items[0] is not TreeViewItem { Tag: null }) return;
                node.Items.Clear();
                await UiWork.AddInBatchesAsync(model.Children, child => node.Items.Add(CreateDirectoryNode(child, 0)));
            };
        }
        return node;
    }

    private Task PreviewAsync(object? selected = null)
    {
        var directory = GetSelectedDirectory(selected) ?? GetSelectedDirectory(_tree.SelectedItem);
        if (directory is null || _cancellation is not null) return Task.CompletedTask;
        _failures.Clear(); _retry.IsEnabled = false;
        _plan = new(directory, 0, 0, [], []);
        _status.Text = "已选择目录。开始后边发现边描述，数量持续更新；已有有效描述自动跳过。";
        _authorization.Text = "将通过 AI 模型设置中的照片分析/视频总结服务处理所选目录及子目录。照片副本、最多 12 帧的视频拼图及已有转写会经过已配置的索引服务；云端配置还会将内容发送给模型服务商。不上传原视频，不修改原文件。开始前会显示实际模型与处理范围。";
        _start.IsEnabled = true;
        return Task.CompletedTask;
    }

    private static string? GetSelectedDirectory(object? selected) => selected switch
    {
        TreeViewItem { Tag: string path } => path,
        string path when Path.IsPathRooted(path) => path,
        _ => null
    };

    private async Task StartAsync(bool retryOnly = false)
    {
        if (_cancellation is not null) return;
        if (_plan is not { } plan)
        {
            _status.Text = _tree.Items.Count == 0
                ? "当前索引没有可选择的目录，请先添加并完成目录索引。"
                : "请先在目录树中单击要处理的目录名称，再开始描述。";
            _tree.Focus();
            return;
        }
        var taskStage = AnalysisStage.Preprocess;
        var pendingDescriptionSync = 0;
        CancellationTokenSource? descriptionSyncCancellation = null;
        Task descriptionSyncTask = Task.CompletedTask;
        void RecordDescriptionWrite()
        {
            if (Interlocked.Increment(ref pendingDescriptionSync) >= 20) FlushDescriptionSync();
        }
        void FlushDescriptionSync()
        {
            if (Interlocked.Exchange(ref pendingDescriptionSync, 0) > 0)
                AppServices.QueueImmediateRemoteSync("directory media description batch");
        }
        (CloudVisionAnalysisService Service, OpenAiCompatibleCloudVisionAnalyzer Analyzer, CloudVisionRunPolicy Policy, string ProviderLabel, bool IsLocal)? photo = null, video = null;
        try
        {
            var retryPaths = retryOnly ? _failures.Where(failure => failure.Retryable).Select(failure => failure.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            _failures.Clear(); _retry.IsEnabled = false;
            _cancellation = new(); _tree.IsEnabled = false; _start.IsEnabled = false; _cancel.IsEnabled = true;
            _concurrency.IsEnabled = false;
            var token = _cancellation.Token;
            descriptionSyncCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            descriptionSyncTask = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
                try
                {
                    while (await timer.WaitForNextTickAsync(descriptionSyncCancellation.Token).ConfigureAwait(false))
                    {
                        FlushDescriptionSync();
                        await RefreshSyncStatusAsync(descriptionSyncCancellation.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (descriptionSyncCancellation.IsCancellationRequested) { }
            }, descriptionSyncCancellation.Token);
            _status.Text = "准备边发现边描述…";
            await RefreshSyncStatusAsync(token);
            photo = AppServices.CreateCloudVisionService(AiFeatureIds.PhotoAnalysis);
            video = AppServices.CreateCloudVisionService(AiFeatureIds.VideoSummary);
            if (photo is null && video is null)
                throw new InvalidOperationException("请先在 AI 模型设置中启用并配置照片分析与视频内容总结服务。");
            var modelText = $"照片：{photo?.ProviderLabel ?? "未配置，遇到待描述照片时将暂停"}\n视频：{video?.ProviderLabel ?? "未配置，遇到待描述视频时将暂停"}";
            if (MessageBox.Show(this, $"{modelText}\n\n将处理所选目录及全部子目录中的照片和视频，边发现边描述，数量持续更新。已有结果跳过；云端模型可能产生费用。\n\n是否开始？",
                "确认目录媒体描述", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            ICloudVisionAnalyzer? photoAnalyzer = photo?.Analyzer;
            if (photoAnalyzer is not null)
                photoAnalyzer = new MediaDescriptionBatch.PhotoInputAnalyzer(photoAnalyzer, Path.Combine(AppPaths.DataRoot, "description-input"),
                    AppServices.ServiceMode ? AppServices.Gateway as IMediaAssetGateway
                        ?? throw new InvalidOperationException("索引服务未提供原图读取接口。") : null);
            var photoService = photoAnalyzer is null ? null : new CloudVisionAnalysisService(AppServices.CloudVisionRepository,
                photoAnalyzer, AppServices.Geocoder, AppServices.People,
                databaseWork: (work, ct) => UiWork.RunDatabaseAsync(work, ct), allowConcurrent: photo?.IsLocal == true);
            var runner = new MediaDescriptionBatch(async (image, ct) =>
            {
                try
                {
                if (photoService is null) throw new AnalysisStageException(AnalysisStage.Model, "model_not_configured", false);
                if (await UiWork.ReadSnapshotAsync("Descriptions.PhotoState", AppServices.Db, reader => new Picer.Core.Vision.CloudVisionRepository(reader).HasCurrentDescription(image), ct)) return true;
                var result = await photoService!.AnalyzeAsync([(image, (IReadOnlyList<VisualConceptScore>)[], "directory-requested")],
                    cancellationToken: ct, runPolicy: photo!.Value.Policy with { MaximumImagesPerRun = 1 });
                if (result.BudgetBlocked > 0) throw new DescriptionBudgetExceededException();
                if (result.Failed > 0)
                {
                    var detail = await UiWork.ReadSnapshotAsync("Descriptions.PhotoState", AppServices.Db, reader => new Picer.Core.Vision.CloudVisionRepository(reader).GetFailureDetail(image.Id, photo!.Value.Analyzer.Provider), ct);
                    throw AnalysisStageException.FromStored(detail);
                }
                if (result.Completed > 0) RecordDescriptionWrite();
                return result.Completed + result.Skipped > 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (DescriptionBudgetExceededException) { throw; }
                catch (AnalysisBudgetExceededException) { throw; }
                catch (Exception exception) { throw AnalysisStageException.Wrap(exception is Microsoft.Data.Sqlite.SqliteException ? AnalysisStage.Save : AnalysisStage.Preprocess, exception); }
            }, async (item, ct) =>
            {
                var stage = AnalysisStage.Preprocess;
                try
                {
                if (video is null) throw new AnalysisStageException(AnalysisStage.Model, "model_not_configured", false);
                if (await UiWork.ReadSnapshotAsync("Descriptions.VideoState", AppServices.Db, reader => new Picer.Core.Video.VideoRepository(reader).HasCurrentSummary(item), ct)) return true;
                var frames = await UiWork.ReadSnapshotAsync("Descriptions.VideoState", AppServices.Db, reader => new Picer.Core.Video.VideoRepository(reader).GetKeyframes(item.Id, existingOnly: !AppServices.ServiceMode), ct);
                if (frames.Count == 0)
                {
                    var backend = AppPaths.LoadIndexBackend();
                    if (backend.BaseUrl is not { Length: > 0 } url) throw new InvalidOperationException("视频关键帧服务尚未配置");
                    using var processor = new ServiceVideoMediaProcessor(new Uri(url), backend.Token ?? "", Path.Combine(AppPaths.DataRoot, "video-keyframes"),
                        AppServices.CreatePinnedServiceClient(backend, TimeSpan.FromMinutes(30)));
                    frames = await processor.ExtractAsync(item, ct);
                    stage = AnalysisStage.Save;
                    await UiWork.RunDatabaseAsync(() => AppServices.Videos.SaveKeyframes(item.Id, frames, item), ct);
                }
                stage = AnalysisStage.Preprocess;
                if (AppServices.ServiceMode) frames = await VideoViewModel.MaterializeRemoteFramesAsync(AppServices.GetClientServices(), frames, item.Id, ct);
                stage = AnalysisStage.Model;
                await AppServices.VideoSummaries.AnalyzeAsync(item, video!.Value.Analyzer, frames, ct, video.Value.Policy);
                stage = AnalysisStage.Save;
                RecordDescriptionWrite();
                return true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (AnalysisBudgetExceededException) { throw; }
                catch (Exception exception) { throw AnalysisStageException.Wrap(stage, exception); }
            });
            var concurrency = photo?.IsLocal == true ? (int)(_concurrency.SelectedItem ?? 2) : 1;
            var videoConcurrency = video?.IsLocal == true ? (int)(_concurrency.SelectedItem ?? 2) : 1;
            taskStage = AnalysisStage.Model;
            var discovery = DirectoryDescriptionDiscovery.DiscoverAsync(AppServices.Db, plan.Directory, retryPaths, token);
            await runner.RunStreamingAsync(discovery, new Progress<string>(value => _status.Text = value), token, concurrency, videoConcurrency,
                new Progress<MediaDescriptionFailure>(failure => _failures.Add(failure)));
            FlushDescriptionSync();
            _status.Text += "；处理结束，失败项可再次运行。";
            await RefreshSyncStatusAsync(token);
        }
        catch (OperationCanceledException) when (_cancellation?.IsCancellationRequested == true) { _status.Text = "已取消；完成的照片和视频描述已保留，再次开始会自动跳过。"; }
        catch (DescriptionBudgetExceededException exception) { _status.Text = exception.Message; }
        catch (DescriptionServiceUnavailableException exception) { _status.Text = exception.Message; }
        catch (AnalysisBudgetExceededException exception) { _status.Text = exception.Message; }
        catch (Exception exception)
        {
            var failure = AnalysisStageException.Wrap(taskStage, exception);
            _failures.Add(new(false, 0, plan.Directory, failure.ErrorCode, failure.Stage, failure.Retryable));
            Picer.Core.Logging.AppLogger.Warn($"Directory description run failed; stage={failure.Stage}; errorType={exception.GetType().Name}; code={failure.ErrorCode}; retryable={failure.Retryable}");
            _status.Text = $"描述任务未完成，错误代码：{failure.ErrorCode}。请查看失败清单并按提示重试。";
        }
        finally
        {
            if (descriptionSyncCancellation is not null)
            {
                await descriptionSyncCancellation.CancelAsync();
                try { await descriptionSyncTask; } catch (OperationCanceledException) { }
                descriptionSyncCancellation.Dispose();
            }
            FlushDescriptionSync();
            photo?.Analyzer.Dispose(); video?.Analyzer.Dispose();
            _cancellation?.Dispose(); _cancellation = null;
            _tree.IsEnabled = true; _cancel.IsEnabled = false;
            _concurrency.IsEnabled = true;
            _start.IsEnabled = _plan is not null;
            _retry.IsEnabled = _failures.Any(failure => failure.Retryable);
        }
    }
}
