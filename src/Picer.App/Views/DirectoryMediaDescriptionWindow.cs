using System.IO;
using System.Collections.ObjectModel;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using Picer.App.Infrastructure;
using Picer.App.ViewModels;
using Picer.Core.Gateway;
using Picer.Core.Indexing;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.App.Views;

public sealed class DirectoryMediaDescriptionWindow : Window
{
    private readonly TreeView _tree = new() { MinHeight = 180 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _authorization = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _start = new() { Content = "开始描述照片和视频", IsEnabled = false, Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _cancel = new() { Content = "取消任务", IsEnabled = false, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Button _retry = new() { Content = "仅重试失败项", IsEnabled = false, Margin = new Thickness(12, 0, 0, 0) };
    private readonly ObservableCollection<MediaDescriptionFailure> _failures = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _previewCancellation;
    private readonly ComboBox _concurrency = new() { ItemsSource = new[] { 1, 2, 3, 4 }, SelectedItem = 2, Width = 70, Margin = new Thickness(8, 0, 0, 0) };
    private readonly CheckBox _fullScan = new() { Content = "完整校验文件内容（较慢；默认只深度扫描新增或变化项）", Margin = new Thickness(0, 4, 0, 8) };
    private CancellationTokenSource? _cancellation;
    private DirectoryMediaDescriptionPlan? _plan;
    private int _selectionVersion;
    private readonly List<DirectoryMediaFailure> _scanFailures = [];

    public DirectoryMediaDescriptionWindow(bool loadOnShow = true)
    {
        Title = "描述目录中的照片和视频";
        Width = 720; Height = 620; MinWidth = 560; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(16) };
        var header = new TextBlock { Text = "选择目录树节点：先扫描该目录及所有子目录，再描述其中的照片和视频。已有有效描述自动跳过。", TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(_authorization); bottom.Children.Add(_status);
        var concurrencyPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 8) };
        concurrencyPanel.Children.Add(new TextBlock { Text = "本地照片/视频并发数（云端为 1）：", VerticalAlignment = VerticalAlignment.Center });
        System.Windows.Automation.AutomationProperties.SetName(_concurrency, "本地照片和视频并发数");
        concurrencyPanel.Children.Add(_concurrency); bottom.Children.Add(concurrencyPanel);
        bottom.Children.Add(_fullScan);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_start); actions.Children.Add(_cancel); actions.Children.Add(_retry); bottom.Children.Add(actions);
        var failures = new ListBox { ItemsSource = _failures, MaxHeight = 110 };
        System.Windows.Automation.AutomationProperties.SetName(failures, "目录描述失败清单");
        bottom.Children.Add(new Expander { Header = "失败清单（取消不计为失败）", Content = failures });
        layout.Children.Add(bottom); layout.Children.Add(_tree); Content = layout;
        System.Windows.Automation.AutomationProperties.SetName(_tree, "选择照片和视频所在目录");
        _tree.SelectedItemChanged += async (_, _) => await PreviewAsync();
        _start.Click += async (_, _) => await StartAsync();
        _retry.Click += async (_, _) => await StartAsync(retryOnly: true);
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        Closing += (_, e) => { if (_cancellation is not null) { _cancellation.Cancel(); e.Cancel = true; _status.Text = "正在取消，已完成描述将保留；结束后可关闭窗口。"; } };
        Closed += (_, _) => { _lifetime.Cancel(); _previewCancellation?.Cancel(); };
        if (loadOnShow) Loaded += async (_, _) =>
        {
            try
            {
                var paths = await UiWork.RunDatabaseAsync(() => AppServices.Db.GetDirectoryTreePaths(_lifetime.Token), _lifetime.Token);
                var roots = await Task.Run(() => DirectoryTreeModel.Build(paths, _lifetime.Token), _lifetime.Token);
                PopulateDirectoryModels(roots);
                _status.Text = "请选择目录；重新运行会继续处理尚未完成的文件。";
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception) { _status.Text = "目录加载失败：" + exception.Message; }
        };
    }

    internal TreeView DirectoryTree => _tree;

    internal void PopulateDirectories(IEnumerable<string> paths) => PopulateDirectoryModels(DirectoryTreeModel.Build(paths));

    private void PopulateDirectoryModels(IReadOnlyList<DirectoryTreeModel> roots)
    {
        _tree.Items.Clear();
        foreach (var root in roots) _tree.Items.Add(CreateDirectoryNode(root, 0));
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

    private async Task PreviewAsync()
    {
        if (_tree.SelectedItem is not TreeViewItem { Tag: string directory } || _cancellation is not null) return;
        var version = ++_selectionVersion;
        _failures.Clear(); _scanFailures.Clear(); _retry.IsEnabled = false;
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose();
        _previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _previewCancellation.Token;
        _plan = null;
        _start.IsEnabled = false;
        try
        {
            var plan = await UiWork.RunDatabaseAsync(() => DirectoryMediaDescriptionPlan.Build(AppServices.Db,
                AppServices.CloudVisionRepository, AppServices.Videos, directory, token), token);
            if (version != _selectionVersion || token.IsCancellationRequested) return;
            _plan = plan;
            _status.Text = $"扫描前索引预览（不含尚未发现的文件）：照片 {plan.TotalPhotos} 张、视频 {plan.TotalVideos} 个；已有描述 {plan.SkippedCount} 项；待描述照片 {plan.Photos.Count} 张、视频 {plan.Videos.Count} 个。开始后扫描并更新实际范围。";
            _authorization.Text = "点击开始，将通过 AI 模型设置中的照片分析/视频总结服务处理全部待描述项。照片副本、最多 12 帧的视频拼图及已有转写会经过已配置的索引服务；远程索引服务也会接收中转数据。云端配置还会将内容发送给模型服务商。不上传原视频，不修改原文件。开始前将显示实际模型与处理范围。";
            _start.IsEnabled = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { if (version == _selectionVersion) _status.Text = "预览失败：" + exception.Message; }
    }

    private async Task StartAsync(bool retryOnly = false)
    {
        if (_plan is not { } plan || _cancellation is not null) return;
        var taskStage = AnalysisStage.Scan;
        (CloudVisionAnalysisService Service, OpenAiCompatibleCloudVisionAnalyzer Analyzer, CloudVisionRunPolicy Policy, string ProviderLabel, bool IsLocal)? photo = null, video = null;
        try
        {
            var retryPaths = retryOnly ? _failures.Where(failure => failure.Retryable).Select(failure => failure.Path).ToArray() : [];
            _failures.Clear(); _retry.IsEnabled = false;
            _previewCancellation?.Cancel();
            _cancellation = new(); _tree.IsEnabled = false; _start.IsEnabled = false; _cancel.IsEnabled = true;
            _fullScan.IsEnabled = false; _concurrency.IsEnabled = false;
            var token = _cancellation.Token;
            await RefreshDirectoryAsync(plan.Directory, token);
            taskStage = AnalysisStage.Preprocess;
            plan = await UiWork.RunDatabaseAsync(() => DirectoryMediaDescriptionPlan.Build(AppServices.Db,
                AppServices.CloudVisionRepository, AppServices.Videos, plan.Directory, token), token);
            if (retryOnly)
            {
                var retryPhotos = plan.Photos.Where(image => retryPaths.Any(path => DirectoryMediaImport.Contains(path, image.Path))).ToArray();
                var retryVideos = plan.Videos.Where(item => retryPaths.Any(path => DirectoryMediaImport.Contains(path, item.Path))).ToArray();
                plan = plan with { DeferredCount = plan.PendingCount - retryPhotos.Length - retryVideos.Length, Photos = retryPhotos, Videos = retryVideos };
            }
            bool Eligible(string path) => !_scanFailures.Any(error => error.IsDirectory
                ? DirectoryMediaImport.Contains(error.Path, path)
                : DirectoryMediaImport.Normalize(error.Path).Equals(DirectoryMediaImport.Normalize(path), StringComparison.OrdinalIgnoreCase));
            plan = plan with { FailedCount = plan.Photos.Count(x => !Eligible(x.Path)) + plan.Videos.Count(x => !Eligible(x.Path)),
                Photos = plan.Photos.Where(x => Eligible(x.Path)).ToArray(), Videos = plan.Videos.Where(x => Eligible(x.Path)).ToArray() };
            _plan = plan;
            if (plan.PendingCount == 0) { _status.Text = "扫描完成，没有可处理的待描述项。"; return; }
            photo = plan.Photos.Count > 0 ? AppServices.CreateCloudVisionService(AiFeatureIds.PhotoAnalysis) : null;
            video = plan.Videos.Count > 0 ? AppServices.CreateCloudVisionService(AiFeatureIds.VideoSummary) : null;
            if (plan.Photos.Count > 0 && photo is null || plan.Videos.Count > 0 && video is null)
                throw new InvalidOperationException("请先在 AI 模型设置中启用并配置照片分析与视频内容总结服务。");
            var modelText = $"照片：{photo?.ProviderLabel ?? "无待处理项"}\n视频：{video?.ProviderLabel ?? "无待处理项"}";
            if (MessageBox.Show(this, $"{modelText}\n\n将描述 {plan.Photos.Count} 张照片和 {plan.Videos.Count} 个视频（含所有子目录）。已有结果跳过；云端模型可能产生费用。\n\n是否开始？",
                "确认目录媒体描述", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            ICloudVisionAnalyzer? photoAnalyzer = photo?.Analyzer;
            if (photoAnalyzer is not null && AppServices.ServiceMode && AppServices.Gateway is IMediaAssetGateway assets)
                photoAnalyzer = new MediaDescriptionBatch.RemotePhotoAnalyzer(photoAnalyzer, assets, Path.Combine(AppPaths.DataRoot, "description-input"));
            var photoService = photoAnalyzer is null ? null : new CloudVisionAnalysisService(AppServices.CloudVisionRepository,
                photoAnalyzer, AppServices.Geocoder, AppServices.People,
                databaseWork: (work, ct) => UiWork.RunDatabaseAsync(work, ct), allowConcurrent: photo?.IsLocal == true);
            var runner = new MediaDescriptionBatch(async (image, ct) =>
            {
                try
                {
                if (await UiWork.RunDatabaseAsync(() => AppServices.CloudVisionRepository.HasCurrentDescription(image), ct)) return true;
                var result = await photoService!.AnalyzeAsync([(image, (IReadOnlyList<VisualConceptScore>)[], "directory-requested")],
                    cancellationToken: ct, runPolicy: photo!.Value.Policy with { MaximumImagesPerRun = 1 });
                if (result.BudgetBlocked > 0) throw new DescriptionBudgetExceededException();
                if (result.Failed > 0)
                {
                    var detail = await UiWork.RunDatabaseAsync(() => AppServices.CloudVisionRepository.GetFailureDetail(image.Id, photo!.Value.Analyzer.Provider), ct);
                    throw AnalysisStageException.FromStored(detail);
                }
                AppServices.QueueImmediateRemoteSync("目录照片描述写入");
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
                if (await UiWork.RunDatabaseAsync(() => AppServices.Videos.HasCurrentSummary(item), ct)) return true;
                var frames = await UiWork.RunDatabaseAsync(() => AppServices.Videos.GetKeyframes(item.Id, existingOnly: !AppServices.ServiceMode), ct);
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
                AppServices.QueueImmediateRemoteSync("目录视频描述写入");
                return true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (AnalysisBudgetExceededException) { throw; }
                catch (Exception exception) { throw AnalysisStageException.Wrap(stage, exception); }
            });
            var concurrency = photo?.IsLocal == true ? (int)(_concurrency.SelectedItem ?? 2) : 1;
            var videoConcurrency = video?.IsLocal == true ? (int)(_concurrency.SelectedItem ?? 2) : 1;
            taskStage = AnalysisStage.Model;
            await runner.RunAsync(plan, new Progress<string>(value => _status.Text = value), token, concurrency, videoConcurrency,
                new Progress<MediaDescriptionFailure>(failure => _failures.Add(failure)));
            _status.Text += "；处理结束，失败项可再次运行。";
        }
        catch (OperationCanceledException) when (_cancellation?.IsCancellationRequested == true) { _status.Text = "已取消；完成的照片和视频描述已保留，再次开始会自动跳过。"; }
        catch (DescriptionBudgetExceededException exception) { _status.Text = exception.Message; }
        catch (AnalysisBudgetExceededException exception) { _status.Text = exception.Message; }
        catch (Exception exception)
        {
            var failure = AnalysisStageException.Wrap(taskStage, exception);
            _failures.Add(new(false, 0, plan.Directory, failure.ErrorCode, failure.Stage, failure.Retryable));
            _status.Text = "描述任务未完成，请查看失败清单并按提示重试。";
        }
        finally
        {
            if (_scanFailures.Count > 0) _status.Text += $" 扫描有 {_scanFailures.Count} 个文件或目录失败，本次跳过，原索引保留；可再次运行重试。";
            photo?.Analyzer.Dispose(); video?.Analyzer.Dispose();
            _cancellation?.Dispose(); _cancellation = null;
            _tree.IsEnabled = true; _cancel.IsEnabled = false;
            _fullScan.IsEnabled = true; _concurrency.IsEnabled = true;
            _start.IsEnabled = _plan is not null;
            _retry.IsEnabled = _failures.Any(failure => failure.Retryable);
        }
    }

    private async Task RefreshDirectoryAsync(string selectedDirectory, CancellationToken token)
    {
        using var mutation = await AppServices.AcquireIndexMutationAsync(token);
        _scanFailures.Clear();
        var indexChanged = false;
        var sources = AppServices.ServiceMode
            ? await AppServices.Gateway.ListSourcesAsync(token)
            : await UiWork.RunDatabaseAsync(AppServices.Db.GetSources, token);
        var matching = sources.Where(source => DirectoryMediaImport.Contains(source.Path, selectedDirectory)
            || DirectoryMediaImport.Contains(selectedDirectory, source.Path)).ToArray();
        if (matching.Length == 0) throw new InvalidOperationException("所选目录不在已配置的媒体源中。");
        var scanner = new DirectoryMediaScanner();
        var backend = AppPaths.LoadIndexBackend();
        using var client = AppServices.ServiceMode ? AppServices.CreatePinnedServiceClient(backend, TimeSpan.FromMinutes(10)) : null;
        foreach (var source in matching)
        {
            var directory = DirectoryMediaImport.Contains(source.Path, selectedDirectory) ? selectedDirectory : DirectoryMediaImport.Normalize(source.Path);
            if (directory.Length == 2 && directory[1] == ':') directory += "/";
            var request = new DirectoryMediaScanRequest(source.Id, directory, ForceFullScan: _fullScan.IsChecked == true);
            if (client is null && !request.ForceFullScan)
                request = request with { KnownEntries = await UiWork.RunDatabaseAsync(() => DirectoryMediaImport.KnownEntries(AppServices.Db, source.Id, directory), token) };
            DirectoryMediaPage page;
            do
            {
                _status.Text = $"正在{(request.ForceFullScan ? "完整校验" : "增量检查")}照片和视频：已检查 {request.Offset} 项…";
                if (client is not null)
                {
                    using var response = await client.PostAsJsonAsync(new Uri(new Uri(backend.BaseUrl!), "/api/service/v1/directory-media"), request, token);
                    response.EnsureSuccessStatusCode();
                    page = await response.Content.ReadFromJsonAsync<DirectoryMediaPage>(cancellationToken: token)
                        ?? throw new InvalidDataException("目录扫描未返回结果。");
                }
                else
                {
                    page = await Task.Run(() => scanner.ReadAsync(request, token), token);
                    page = page with { IndexChanged = await UiWork.RunDatabaseAsync(() => DirectoryMediaImport.ApplyAndDetectChanges(AppServices.Db, source.Id, directory, page), token) };
                }
                indexChanged |= page.IndexChanged;
                foreach (var failure in page.Failures ?? [])
                    if (!_scanFailures.Contains(failure))
                    {
                        _scanFailures.Add(failure);
                        _failures.Add(new(VideoFormats.IsSupported(failure.Path), 0, failure.Path,
                            $"{(failure.IsDirectory ? "目录" : "文件")}扫描失败（{failure.Code}）", AnalysisStage.Scan,
                            !failure.Code.Contains("Unauthorized", StringComparison.Ordinal) && !failure.Code.Contains("link", StringComparison.OrdinalIgnoreCase)));
                    }
                if (!page.Done && page.NextOffset <= request.Offset) throw new InvalidDataException("目录扫描进度异常。");
                request = request with { Snapshot = page.Snapshot, Offset = page.NextOffset, KnownEntries = null };
            } while (!page.Done);
        }
        if (indexChanged || !mutation.RestoreUnchangedMirror())
        {
            _status.Text = "扫描完成，正在同步新增或变化的索引…";
            await AppServices.RefreshMirrorAfterIndexMutationAsync(token);
        }
    }
}
