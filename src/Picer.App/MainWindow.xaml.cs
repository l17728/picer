using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Picer.App.ViewModels;
using Picer.Core.Search;
using Picer.Core.Logging;

namespace Picer.App;

public partial class MainWindow : Window
{
    private void RepairPhotoIndex_Click(object sender, RoutedEventArgs e)
    {
        new Views.IndexRepairWindow { Owner = this }.ShowDialog();
        _mainVm?.LoadImages();
    }
    private Point _faceDragStartPoint;
    private int _faceDragStartIndex = -1;
    private bool _faceDragSelecting;
    private HashSet<object> _faceSelectionBeforeDrag = [];
    private readonly MainViewModel _mainVm;
    private bool _initialized;
    private readonly bool _remoteMode;
    private ServiceBrowseViewModel? _remoteBrowseViewModel;
    private Views.ServiceBrowseWindow? _remoteBrowseWindow;

    public MainWindow()
    {
        InitializeComponent();
        _mainVm = null!;
        _remoteMode = true;
        Title = "Picer - 图片管理（远程）";
        DataContext = null;
    }

    public MainWindow(MainViewModel mainVm)
    {
        InitializeComponent();
        
        _mainVm = mainVm;
        DataContext = mainVm;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        AiModelSettingsMenuItem.IsEnabled = !AppServices.ServiceMode;
        if (_remoteMode)
        {
            var browse = new Views.ServiceBrowseWindow(AppServices.Gateway, autoLoad: false);
            _remoteBrowseViewModel = browse.ViewModel;
            _remoteBrowseWindow = browse;
            LocalToolbar.Visibility = Visibility.Collapsed;
            if (browse.Content is FrameworkElement content)
            {
                content.DataContext = _remoteBrowseViewModel;
                RemoteContent.Content = content;
                RemoteContent.Visibility = Visibility.Visible;
                MainTabs.Visibility = Visibility.Collapsed;
            }
            await _remoteBrowseViewModel.SearchAsync();
            return;
        }
        await _mainVm.InitializeAsync();
        var missing = new RuntimeComponentService(AppPaths.RuntimeRoot).GetMissingComponentNames();
        if (missing.Count > 0)
            _mainVm.StatusText = $"发布包缺少 {missing.Count} 个运行组件：{string.Join("、", missing.Take(3))}{(missing.Count > 3 ? "…" : "")}。请打开“运行组件”查看。";
    }

    private void SourcesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is SourceNodeViewModel node)
            _mainVm.SelectedSource = node;
    }

    private void PeopleFacesList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) <= .01 && Math.Abs(e.HorizontalChange) <= .01) return;
        if (sender is not ListBox list) return;
        var panel = FindVisualChild<Controls.VirtualizingWrapPanel>(list);
        var range = panel?.GetViewportItemRange() ?? (-1, -1);
        var visible = range.First >= 0
            ? Enumerable.Range(range.First, Math.Max(0, range.Last - range.First + 1))
            : Enumerable.Empty<int>();
        _mainVm.People.ScheduleDisplayedFaceThumbnails(visible);
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_mainVm is null || MainTabs is null || sender is not TextBox box) return;
        // IME composition can update TextBox.Text before its binding source. Synchronize
        // explicitly so Chinese person names do not require Enter or a focus change.
        if (!string.Equals(_mainVm.SearchQuery, box.Text, StringComparison.Ordinal))
            _mainVm.SearchQuery = box.Text;
        if (MediaTypeBox?.SelectedIndex is 1 or 2)
        {
            _mainVm.ReloadDirectoryMedia();
            MainTabs.SelectedIndex = 0;
        }
        else if (box.Text.Length > 0) MainTabs.SelectedIndex = 1;
    }

    private void RegionSearch_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 1;
    }

    private async void AiVideoResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: long videoId }) return;
        await _mainVm.Video.FocusVideoAsync(videoId);
        MainTabs.SelectedIndex = 4;
    }

    private void QuestionMode_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 1;
        if (_mainVm.IsQuestionMode)
        {
            AiQuestionBox.Focus();
            AiQuestionBox.CaretIndex = AiQuestionBox.Text.Length;
        }
    }

    private void AiQuestionBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        // Do not await the long-running model request from the WPF key event.
        // The mouse command is already fire-and-forget from Button.Command;
        // keeping the keyboard path identical prevents an apparent UI hang.
        if (!_mainVm.IsAiQuestionRunning)
            _ = _mainVm.SubmitAiQuestionAsync();
    }

    private void MediaTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mainVm is null || MainTabs is null || MediaTypeBox is null) return;
        _mainVm.SetDirectoryMediaFilter(MediaTypeBox.SelectedIndex);
        MainTabs.SelectedIndex = 0;
    }

    private void CloudVisionSettings_Click(object sender, RoutedEventArgs e)
    {
        var serviceManaged = AppServices.ServiceMode;
        if (!serviceManaged)
        {
            try
            {
                serviceManaged = Picer.Core.Gateway.IndexBackendConfig.LoadConfiguredServiceEndpoint(
                    AppPaths.ServiceEndpointPath,
                    static cipher => System.Security.Cryptography.ProtectedData.Unprotect(
                        cipher, null, System.Security.Cryptography.DataProtectionScope.CurrentUser)) is not null;
            }
            catch (Exception exception)
            {
                AppLogger.Warn("AI settings service ownership probe failed: " + exception.GetType().Name);
            }
        }
        if (serviceManaged)
        {
            MessageBox.Show(this, "本地与远程模式的模型、端点和密钥均由统一 Service 管理，客户端不可修改。", "AI 模型设置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new CloudVisionSettingsWindow(AppPaths.CloudVisionSettingsPath) { Owner = this }.ShowDialog();
    }

    private void VoiceAssistant_Click(object sender, RoutedEventArgs e)
    {
        var chat = AppServices.CreateAiChatClient(AiFeatureIds.AiQuestion);
        if (chat is null) { MessageBox.Show(this, "请先在 AI 模型设置中为 AI 问答选择可用的模型。", "语音助手"); return; }
        Uri? asrEndpoint; string speechModel; Uri? ttsEndpoint;
        var backend = AppPaths.LoadIndexBackend();
        if (string.IsNullOrWhiteSpace(backend.BaseUrl))
        {
            try
            {
                backend = Picer.Core.Gateway.IndexBackendConfig.LoadConfiguredServiceEndpoint(
                    AppPaths.ServiceEndpointPath,
                    static cipher => System.Security.Cryptography.ProtectedData.Unprotect(
                        cipher, null, System.Security.Cryptography.DataProtectionScope.CurrentUser))
                    ?? backend;
            }
            catch (Exception exception)
            {
                AppLogger.Warn("Local voice relay endpoint unavailable: " + exception.GetType().Name);
            }
        }
        if (string.IsNullOrWhiteSpace(backend.BaseUrl) || !Uri.TryCreate(backend.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var serviceBase))
        {
            chat.Dispose();
            MessageBox.Show(this, "本地与远程模式都必须先配置 IndexService；模型端点由驻留代理统一提供。", "语音助手");
            return;
        }
        asrEndpoint = new Uri(serviceBase, "api/service/v1/asr");
        ttsEndpoint = new Uri(serviceBase, "api/service/v1/tts");
        speechModel = "service-asr";
        var window = new Views.VoiceAssistantWindow(new VoiceCommandAgent(chat),
            _mainVm.GetVoiceLocationPaths,
            // Voice planning only exposes manually confirmed people. Candidate
            // clusters and unassigned/automatic suggestions must not enter the
            // LLM context.
            () => AppServices.People.GetAll()
                .Where(person => person.ConfirmedFaceCount > 0)
                .Select(person => person.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(), ExecuteVoiceActionAsync,
            asrEndpoint!, speechModel, ttsEndpoint!)
            { Owner = this };
        window.Closed += (_, _) => chat.Dispose();
        window.Show();
    }

    private async Task ExecuteVoiceActionAsync(VoiceAction action)
    {
        switch (action.Kind)
        {
            case VoiceActionKind.OpenLocation:
                MainTabs.SelectedIndex = 0;
                var opened = action.Target is not null && _mainVm.OpenVoiceLocation(action.Target);
                AppLogger.Info($"Voice location action result; success={opened}; targetPresent={action.Target is not null}");
                if (!opened) throw new InvalidOperationException("语音指定的地点未找到，未执行目录跳转");
                break;
            case VoiceActionKind.OpenModule:
                MainTabs.SelectedIndex = action.Target switch { "目录浏览" => 0, "搜索" => 1, "人物" => 2, "标签与相册" => 3, "视频" => 4, _ => MainTabs.SelectedIndex };
                break;
            case VoiceActionKind.SearchMedia:
                _mainVm.IsQuestionMode = true; _mainVm.IncludeVideosInAiQuestion = action.IncludeVideos;
                _mainVm.AiQuestionInput = action.Query ?? ""; MainTabs.SelectedIndex = 1;
                await _mainVm.SubmitAiQuestionAsync();
                break;
        }
    }

    private void CloudVisionTasks_Click(object sender, RoutedEventArgs e)
    {
        new CloudVisionTaskCenterWindow(_mainVm) { Owner = this }.Show();
    }

    private void RuntimeComponents_Click(object sender, RoutedEventArgs e)
    {
        new Views.RuntimeComponentsWindow { Owner = this }.ShowDialog();
    }

    private void IndexService_Click(object sender, RoutedEventArgs e)
    {
        new Views.IndexServiceWindow { Owner = this }.ShowDialog();
    }

    private void BackendSettings_Click(object sender, RoutedEventArgs e)
    {
        new Views.BackendSettingsWindow { Owner = this }.ShowDialog();
    }

    private void ClipSemantic_Click(object sender, RoutedEventArgs e)
    {
        new Views.ClipSemanticWindow(AppServices.EnsureClipSemantic(), AppServices.ClipRepository, AppServices.ClipUnavailableReason)
            { Owner = this }.Show();
    }

    private void FlorenceValidation_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "Florence 视觉能力必须由 IndexService/MediaAgent 提供，当前客户端不直接加载模型。", "服务能力未接入", MessageBoxButton.OK, MessageBoxImage.Information);
        return;
    }

    private void FlorenceBatch_Click(object sender, RoutedEventArgs e)
    {
        DescribeMediaDirectory_Click(sender, e);
    }

    private void DescribeMediaDirectory_Click(object sender, RoutedEventArgs e) =>
        new Views.DirectoryMediaDescriptionWindow { Owner = this }.ShowDialog();

    private void VideoMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VideoViewModel { SelectedVideo: { } selected } })
            new Views.VideoMetadataWindow(AppServices.Db, selected.Record.Id,
                () => AppServices.QueueImmediateRemoteSync("视频标签与概念修改")) { Owner = this }.ShowDialog();
    }

    private void StorageSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new StorageSettingsWindow { Owner = this };
        if (window.ShowDialog() == true && window.Applied) _mainVm.ReplaceIndexEngine(AppServices.Index);
    }

    private void DataProtection_Click(object sender, RoutedEventArgs e)
    {
        new Views.DataProtectionWindow(new DataProtectionService(AppServices.Db, AppPaths.DataRoot)) { Owner = this }.ShowDialog();
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var service = new DiagnosticService(AppServices.Db, AppServices.Storage, AppServices.Thumbnails, AppPaths.RuntimeRoot, AppPaths.DataRoot);
        new Views.DiagnosticCenterWindow(service) { Owner = this }.Show();
    }

    private void FaceValidation_Click(object sender, RoutedEventArgs e)
    {
        new Views.FaceValidationWindow(AppServices.FaceValidation, AppServices.FaceTraining, AppServices.VisionAnalysis, AppServices.VisionUnavailableReason, AppServices.VisionRepository)
            { Owner = this }.ShowDialog();
    }

    private async void CreateEmptyPerson_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PeopleViewModel people })
            await people.CreateEmptyPersonFromUiAsync();
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs) || DataContext is not MainViewModel) return;
        if (MainTabs.SelectedIndex == 2) await _mainVm.People.InitializePageAsync();
        else if (MainTabs.SelectedIndex == 3) await _mainVm.Semantic.InitializePageAsync();
    }

    private void VideoGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid)
            _mainVm.Video.UpdateSelection(grid.SelectedItems.Cast<VideoItemViewModel>());
    }

    private void VideoFaces_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list)
            _mainVm.Video.UpdateVideoFaceSelection(list.SelectedItems.Cast<VideoFaceItemViewModel>());
    }

    private void VideoFaceReviewBadge_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VideoItemViewModel video) return;
        _mainVm.Video.SelectedVideo = video;
        VideoGrid.SelectedItem = video;
        VideoGrid.ScrollIntoView(video);
        VideoDetailTabs.SelectedIndex = 1;
        e.Handled = true;
    }

    private void RejectVideoSuggestedFace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor || _mainVm.Video.SelectedVideoFace is not { } face) return;
        var menu = new ContextMenu();
        foreach (var person in _mainVm.Video.VideoPeople.Where(person => person.Id != face.Record.SuggestedPersonId))
        {
            var target = person;
            var item = new MenuItem { Header = target.DisplayName };
            item.Click += async (_, _) => await _mainVm.Video.AssignSelectedVideoFacesToPersonAsync(target);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "创建新人物并归入…" };
        create.Click += async (_, _) =>
        {
            var dialog = new Views.CreatePersonWindow { Owner = this };
            if (dialog.ShowDialog() == true) await _mainVm.Video.CreatePersonAndAssignSelectedVideoFacesAsync(dialog.PersonName);
        };
        menu.Items.Add(create);
        var pending = new MenuItem { Header = "否定推荐，保留待确认" };
        pending.Click += async (_, _) => await _mainVm.Video.RejectVideoFaceSuggestionCommand.ExecuteAsync(null);
        menu.Items.Add(pending);
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async void ConfirmVideoSuggestedFace_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VideoFaceItemViewModel face) return;
        _mainVm.Video.SelectedVideoFace = face;
        _mainVm.Video.UpdateVideoFaceSelection([face]);
        await _mainVm.Video.ConfirmVideoFaceCommand.ExecuteAsync(null);
        e.Handled = true;
    }

    private void RejectVideoSuggestedCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VideoFaceItemViewModel face) return;
        _mainVm.Video.SelectedVideoFace = face;
        _mainVm.Video.UpdateVideoFaceSelection([face]);
        RejectVideoSuggestedFace_Click(sender, e);
    }

    private async void PeopleFace_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindDataContext<UnassignedFaceViewModel>(e.OriginalSource as DependencyObject) is { } face)
            await _mainVm.People.OpenFacePhotoAsync(face);
    }

    private async void PeopleFacesList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _mainVm.People.DisplayedCollectionKind == DisplayedFaceCollectionKind.Ignored) return;
        var faces = PeopleFacesList.SelectedItems.Cast<UnassignedFaceViewModel>().ToArray();
        if (faces.Length == 0 && _mainVm.People.SelectedDisplayedFace is { } selected) faces = [selected];
        if (faces.Length == 0) return;
        e.Handled = true;
        await _mainVm.People.IgnoreFacesAsync(faces);
    }

    private async void ConfirmSuggestedFace_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is UnassignedFaceViewModel face)
            await _mainVm.People.ConfirmAutomaticSuggestionAsync(face);
        e.Handled = true;
    }

    private async void RejectSuggestedFace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor || anchor.DataContext is not UnassignedFaceViewModel face) return;
        var menu = new ContextMenu();
        foreach (var person in _mainVm.People.People.Where(person => person.Id != face.Record.SuggestedPersonId))
        {
            var target = person;
            var item = new MenuItem { Header = target.DisplayName };
            item.Click += async (_, _) => await _mainVm.People.MoveFaceToPersonAsync(face, target);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "创建新人物并归入…" };
        create.Click += async (_, _) =>
        {
            var dialog = new Views.CreatePersonWindow { Owner = this };
            if (dialog.ShowDialog() == true) await _mainVm.People.CreatePersonWithFacesAsync(dialog.PersonName, [face]);
        };
        menu.Items.Add(create);
        var pending = new MenuItem { Header = "保留在待分类" };
        menu.Items.Add(pending);
        var ignored = new MenuItem { Header = "标记为不分类" };
        ignored.Click += async (_, _) => await _mainVm.People.IgnoreFaceAsync(face);
        menu.Items.Add(ignored);
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
        await _mainVm.People.RejectAutomaticSuggestionAsync(face);
        e.Handled = true;
    }

    private void PeopleFace_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { } item)
        {
            if (!item.IsSelected) PeopleFacesList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void CandidateCluster_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { } item)
            item.IsSelected = true;
    }

    private void CandidateClusterMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        menu.Items.Clear();
        if (CandidateClustersList.SelectedItem is not CandidateClusterViewModel cluster) return;
        var ignore = new MenuItem { Header = $"整组标记为不分类（{cluster.FaceCount} 张）" };
        ignore.Click += async (_, _) => await _mainVm.People.IgnoreCandidateClusterAsync(cluster);
        menu.Items.Add(ignore);
    }

    private void PeopleFace_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } item ||
            item.DataContext is not UnassignedFaceViewModel) return;
        _faceDragStartPoint = e.GetPosition(list);
        _faceDragStartIndex = list.ItemContainerGenerator.IndexFromContainer(item);
        _faceDragSelecting = false;
        _faceSelectionBeforeDrag = list.SelectedItems.Cast<object>().ToHashSet();
    }

    private void PeopleFace_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox list || e.LeftButton != MouseButtonState.Pressed || _faceDragStartIndex < 0) return;
        var point = e.GetPosition(list);
        if (!_faceDragSelecting && Math.Abs(point.X - _faceDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _faceDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _faceDragSelecting = true;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } item) return;
        var currentIndex = list.ItemContainerGenerator.IndexFromContainer(item);
        if (currentIndex < 0) return;
        var additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        list.SelectedItems.Clear();
        if (additive)
            foreach (var selected in _faceSelectionBeforeDrag.Where(list.Items.Contains)) list.SelectedItems.Add(selected);
        var first = Math.Min(_faceDragStartIndex, currentIndex);
        var last = Math.Max(_faceDragStartIndex, currentIndex);
        for (var index = first; index <= last; index++)
            if (list.Items[index] is UnassignedFaceViewModel face && !list.SelectedItems.Contains(face))
                list.SelectedItems.Add(face);
        e.Handled = true;
    }

    private void PeopleFace_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _faceDragStartIndex = -1;
        _faceDragSelecting = false;
        _faceSelectionBeforeDrag.Clear();
    }

    private void PeopleFaceMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        menu.Items.Clear();
        if (_mainVm.People.SelectedDisplayedFace is not { } face) return;
        var faces = PeopleFacesList.SelectedItems.Cast<UnassignedFaceViewModel>().ToArray();
        if (faces.Length == 0) faces = [face];
        var countText = faces.Length > 1 ? $"（{faces.Length} 张）" : "";

        var open = new MenuItem { Header = "打开关联照片" };
        open.Click += async (_, _) => await _mainVm.People.OpenFacePhotoAsync(face);
        menu.Items.Add(open);

        var kind = _mainVm.People.DisplayedCollectionKind;
        var move = new MenuItem { Header = $"移到已有分类{countText}" };
        foreach (var person in _mainVm.People.People)
        {
            var target = person;
            if (kind == DisplayedFaceCollectionKind.NamedPerson && _mainVm.People.SelectedPerson?.Id == target.Id)
                continue;
            var item = new MenuItem { Header = target.DisplayName };
            item.Click += async (_, _) => await _mainVm.People.MoveFacesToPersonAsync(faces, target);
            move.Items.Add(item);
        }
        if (move.Items.Count > 0) menu.Items.Add(move);

        var create = new MenuItem { Header = $"创建新人物并归入{countText}…" };
        create.Click += async (_, _) =>
        {
            var dialog = new Views.CreatePersonWindow { Owner = this };
            if (dialog.ShowDialog() == true)
                await _mainVm.People.CreatePersonWithFacesAsync(dialog.PersonName, faces);
        };
        menu.Items.Add(create);

        if (kind != DisplayedFaceCollectionKind.Unassigned)
        {
            var unassigned = new MenuItem { Header = $"移到待分类{countText}" };
            unassigned.Click += async (_, _) => await _mainVm.People.MoveFacesToUnassignedAsync(faces);
            menu.Items.Add(unassigned);
        }
        if (kind != DisplayedFaceCollectionKind.Ignored)
        {
            var ignored = new MenuItem { Header = $"标记为不分类{countText}" };
            ignored.Click += async (_, _) => await _mainVm.People.IgnoreFacesAsync(faces);
            menu.Items.Add(ignored);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null && element is not T)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        return element as T;
    }

    private static T? FindDataContext<T>(DependencyObject? element) where T : class =>
        FindAncestor<FrameworkElement>(element)?.DataContext as T;

    private async void DirectoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is DirectoryNodeViewModel node)
        {
            _mainVm.SelectedDirectory = node;
            if (!node.IsSceneAnchor && !node.IsAlbumRoot && !node.IsGeoNode && node.AlbumId is null)
            {
                await _mainVm.ExpandDirectoryCommand.ExecuteAsync(node);
                await _mainVm.LoadSceneAnchorsAsync(node);
            }
        }
    }

    private void DirectoryTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? element=e.OriginalSource as DependencyObject;
        while(element is not null&&element is not TreeViewItem)element=System.Windows.Media.VisualTreeHelper.GetParent(element);
        if(element is TreeViewItem item)item.IsSelected=true;
    }
    private void DirectoryContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var selected = DirectoryTreeView.SelectedItem as DirectoryNodeViewModel;
        var mode = selected switch
        {
            { IsPendingLocation: true } => "pending-location",
            { AlbumId: not null } => "album",
            { IsSceneAnchor: true } => "scene",
            { IsAlbumRoot: false, IsGeoNode: false } => "directory",
            _ => null
        };
        foreach (var item in menu.Items.OfType<FrameworkElement>())
            item.Visibility = string.Equals(item.Tag as string, mode, StringComparison.Ordinal)
                ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void DeleteAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (DirectoryTreeView.SelectedItem is DirectoryNodeViewModel { AlbumId: not null } album)
            await _mainVm.DeleteAlbumAsync(album);
    }
    private async void ParsePendingLocations_Click(object sender, RoutedEventArgs e) =>
        await _mainVm.ParsePendingLocationsAsync();
    private async void DirectoryLocalClassification_Click(object sender,RoutedEventArgs e){if(DirectoryTreeView.SelectedItem is DirectoryNodeViewModel node)await _mainVm.SetDirectoryClassificationModeAsync(node,false);}
    private async void DirectoryRecursiveClassification_Click(object sender,RoutedEventArgs e){if(DirectoryTreeView.SelectedItem is DirectoryNodeViewModel node)await _mainVm.SetDirectoryClassificationModeAsync(node,true);}
    private async void DescribeDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (DirectoryTreeView.SelectedItem is not DirectoryNodeViewModel node) return;
        try { await _mainVm.DescribeDirectoryAsync(node); }
        catch (Exception exception) { MessageBox.Show(this, "目录描述失败：" + exception.Message, "目录照片描述", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async void RegenerateDirectoryClassification_Click(object sender,RoutedEventArgs e){if(DirectoryTreeView.SelectedItem is DirectoryNodeViewModel node)await _mainVm.LoadSceneAnchorsAsync(node,true);}
    private async void DescribeUnclassifiedScene_Click(object sender,RoutedEventArgs e){if(DirectoryTreeView.SelectedItem is DirectoryNodeViewModel node)await _mainVm.DescribeUnclassifiedSceneAsync(node);}
    private async void EditSceneDescription_Click(object sender,RoutedEventArgs e){if(DirectoryTreeView.SelectedItem is DirectoryNodeViewModel node)await _mainVm.EditSceneDescriptionAsync(node);}

    private void ImagesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_mainVm.SelectedImage is not null)
            _mainVm.OpenSelectedCommand.Execute(null);
    }

    private void KeyboardHelp_Click(object sender, RoutedEventArgs e) =>
        new KeyboardHelpWindow { Owner = this }.ShowDialog();

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            KeyboardHelp_Click(sender, e);
            return;
        }
        if (_remoteMode)
        {
            e.Handled = _remoteBrowseWindow?.HandleShortcut(e.Key, Keyboard.Modifiers) == true;
            return;
        }
        e.Handled = HandleLocalShortcut(e.Key, Keyboard.Modifiers);
    }

    internal bool HandleLocalShortcut(Key key, ModifierKeys modifiers)
    {
        if (_mainVm is null) return false;
        if (modifiers == ModifierKeys.Control)
        {
            if (key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); return true; }
            if (key == Key.L) { MainTabs.SelectedIndex = 0; DirectoryTreeView.Focus(); return true; }
            if (key >= Key.D1 && key <= Key.D5)
            {
                MainTabs.SelectedIndex = key - Key.D1;
                ((TabItem)MainTabs.SelectedItem).Focus();
                return true;
            }
        }
        if (modifiers != ModifierKeys.None || !SearchBox.IsKeyboardFocusWithin) return false;
        if (key == Key.Escape) { _mainVm.ClearSearchCommand.Execute(null); return true; }
        if (key == Key.Enter) { _ = _mainVm.ResolvePlaceCommand.ExecuteAsync(SearchBox.Text); return true; }
        return false;
    }
}
