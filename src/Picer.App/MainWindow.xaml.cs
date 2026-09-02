using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Picer.App.ViewModels;

namespace Picer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _mainVm;
    private bool _initialized;

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

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (MainTabs is null || sender is not TextBox box) return;
        if (MediaTypeBox?.SelectedIndex == 1)
        {
            _ = _mainVm.Video.ApplySearchAsync(box.Text);
            MainTabs.SelectedIndex = 4;
        }
        else if (box.Text.Length > 0) MainTabs.SelectedIndex = 1;
    }

    private void RegionSearch_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 1;
    }

    private void QuestionMode_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 1;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private void MediaTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabs is null || MediaTypeBox is null) return;
        if (MediaTypeBox.SelectedIndex == 1)
        {
            MainTabs.SelectedIndex = 4;
            _ = _mainVm.Video.LoadAsync();
        }
        else if (MainTabs.SelectedIndex == 4)
        {
            MainTabs.SelectedIndex = 0;
        }
    }

    private void CloudVisionSettings_Click(object sender, RoutedEventArgs e)
    {
        new CloudVisionSettingsWindow(AppPaths.CloudVisionSettingsPath) { Owner = this }.ShowDialog();
    }

    private void CloudVisionTasks_Click(object sender, RoutedEventArgs e)
    {
        new CloudVisionTaskCenterWindow(_mainVm) { Owner = this }.Show();
    }

    private void RuntimeComponents_Click(object sender, RoutedEventArgs e)
    {
        new Views.RuntimeComponentsWindow { Owner = this }.ShowDialog();
    }

    private void ClipSemantic_Click(object sender, RoutedEventArgs e)
    {
        new Views.ClipSemanticWindow(AppServices.EnsureClipSemantic(), AppServices.ClipRepository, AppServices.ClipUnavailableReason)
            { Owner = this }.Show();
    }

    private void FlorenceValidation_Click(object sender, RoutedEventArgs e)
    {
        new Views.FlorenceValidationWindow(new Picer.Core.Vision.FlorenceRuntimeValidationService(AppPaths.FlorenceModelRoot), AppPaths.FlorenceRuntimeSettingsPath)
            { Owner = this }.Show();
    }

    private void FlorenceBatch_Click(object sender, RoutedEventArgs e)
    {
        new Views.FlorenceBatchWindow(AppServices.FlorenceResults, AppPaths.FlorenceModelRoot, AppPaths.FlorenceRuntimeSettingsPath)
            { Owner = this }.Show();
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
        new Views.FaceValidationWindow(AppServices.FaceValidation, AppServices.VisionAnalysis, AppServices.VisionUnavailableReason)
            { Owner = this }.ShowDialog();
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs) || DataContext is not MainViewModel) return;
        if (MainTabs.SelectedIndex == 2) await _mainVm.People.InitializePageAsync();
        else if (MainTabs.SelectedIndex == 3) await _mainVm.Semantic.InitializePageAsync();
    }

    private async void PeopleFace_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindDataContext<UnassignedFaceViewModel>(e.OriginalSource as DependencyObject) is { } face)
            await _mainVm.People.OpenFacePhotoAsync(face);
    }

    private void PeopleFace_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { } item)
            item.IsSelected = true;
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
    private async void RegenerateDirectoryClassification_Click(object sender,RoutedEventArgs e){if(DirectoryTreeView.SelectedItem is DirectoryNodeViewModel node)await _mainVm.LoadSceneAnchorsAsync(node,true);}
    private async void DescribeUnclassifiedScene_Click(object sender,RoutedEventArgs e){if(DirectoryTreeView.SelectedItem is DirectoryNodeViewModel node)await _mainVm.DescribeUnclassifiedSceneAsync(node);}
    private async void EditSceneDescription_Click(object sender,RoutedEventArgs e){if(DirectoryTreeView.SelectedItem is DirectoryNodeViewModel node)await _mainVm.EditSceneDescriptionAsync(node);}

    private void ImagesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_mainVm.SelectedImage is not null)
            _mainVm.OpenSelectedCommand.Execute(null);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
        {
            _mainVm.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SearchBox.IsKeyboardFocusWithin)
        {
            _ = _mainVm.ResolvePlaceCommand.ExecuteAsync(SearchBox.Text);
            e.Handled = true;
        }
    }
}
