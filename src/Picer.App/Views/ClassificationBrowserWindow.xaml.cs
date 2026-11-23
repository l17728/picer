using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Picer.Core.Classification;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.App.ViewModels;

namespace Picer.App.Views;

public partial class ClassificationBrowserWindow : Window
{
    private readonly ClassificationV2Client _client = new();
    private readonly CancellationTokenSource _closed = new();
    private ClassificationTaxonomySnapshot? _taxonomy;
    private ClassificationBrowseResult? _browse;
    private string? _selectedLabelId;
    private IReadOnlyList<string> _selectedFilterIds = Array.Empty<string>();
    private readonly ObservableCollection<PhotoRow> _rows = new();
    private bool _loadingMore;
    private bool _restoringSelection;

    public ClassificationBrowserWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { _closed.Cancel(); _client.Dispose(); _closed.Dispose(); };
    }

    // rebuildTree: the taxonomy and the per-label counts do not depend on which node is selected,
    // so selecting a node only reloads the photo list. The tree is rebuilt only when its nodes or
    // counts can have changed (first load, filter toggles, refresh, decisions, scan).
    private async Task RefreshAsync(bool rebuildTree = true)
    {
        try
        {
            _browse = await BrowsePage(null);
            _taxonomy = _browse.Taxonomy;
            if (rebuildTree) BuildLabelTree();
            _rows.Clear();
            foreach (var photo in _browse.Photos) _rows.Add(new PhotoRow(photo, _taxonomy, _closed.Token));
            Photos.ItemsSource = _rows;
            UpdateStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Feedback.Text = "分类读取失败：" + ex.Message; }
    }

    private Task<ClassificationBrowseResult> BrowsePage(string? cursor)
    {
        var filter = new ClassificationFilter(
            _selectedFilterIds.Count == 0
                ? Array.Empty<ClassificationFilterGroup>()
                : new[] { new ClassificationFilterGroup(ClassificationMatchMode.Any, _selectedFilterIds) },
            Array.Empty<string>());
        return _client.Browse(new ClassificationBrowseRequest(filter,
            IncludeSuggestions.IsChecked == true, IncludeLegacy.IsChecked == true, Cursor: cursor), _closed.Token);
    }

    private void UpdateStatus()
    {
        if (_browse is null || _taxonomy is null) return;
        var name = _selectedLabelId is null ? "全部分类" : _taxonomy.Labels.FirstOrDefault(x => x.Id == _selectedLabelId)?.Name ?? _selectedLabelId;
        StatusText.Text = $"{name}：已加载 {_rows.Count} / {_browse.Matched} 张照片" + (_browse.NextCursor is null ? "" : "（继续滚动自动加载）");
    }

    private async Task LoadMoreAsync()
    {
        if (_loadingMore || _browse?.NextCursor is null) return;
        _loadingMore = true;
        try
        {
            var page = await BrowsePage(_browse.NextCursor);
            _browse = page;
            foreach (var photo in page.Photos) _rows.Add(new PhotoRow(photo, _taxonomy ?? page.Taxonomy, _closed.Token));
            UpdateStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Feedback.Text = "继续加载失败：" + ex.Message; }
        finally { _loadingMore = false; }
    }

    private void BuildLabelTree()
    {
        if (_taxonomy is null) return;
        // A rebuild must never undo what the user did to the tree: remember which nodes were
        // expanded or collapsed and which one was selected, then put that state back.
        var wasExpanded = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in TreeNodes())
            if (item.Tag is string[] ids) wasExpanded[string.Join("|", ids)] = item.IsExpanded;
        var selectedKey = LabelTree.SelectedItem is TreeViewItem { Tag: string[] selectedIds } ? string.Join("|", selectedIds) : null;
        LabelTree.Items.Clear();
        var counts = _browse?.Counts;
        // A node stays visible while it is selected even if its count drops to zero, so a live
        // selection never disappears from under the user.
        bool VisibleLeaf(ClassificationLabel label) =>
            counts is null || label.Id == _selectedLabelId || _selectedFilterIds.Contains(label.Id) || counts.GetValueOrDefault(label.Id) > 0;
        var roots = new List<TreeViewItem>();
        foreach (var group in _taxonomy.Labels.Where(x => x.Active).GroupBy(x => x.Dimension).OrderBy(x => x.Key))
        {
            var groupIds = group.Where(x => x.Assignable && !x.Id.StartsWith("nav.", StringComparison.Ordinal))
                .Select(x => x.Id).Distinct(StringComparer.Ordinal).ToArray();
            var dimensionCount = counts?.GetValueOrDefault(ClassificationCountKeys.Dimension(group.Key)) ?? 0;
            if (counts is not null && dimensionCount == 0 && !groupIds.Any(_selectedFilterIds.Contains)) continue;
            var root = new TreeViewItem { Header = $"{group.Key}（{dimensionCount}）", IsExpanded = true, Tag = groupIds };
            var people = group.Where(x => x.Id.StartsWith("person.confirmed.", StringComparison.Ordinal)).OrderBy(x => x.Name)
                .Where(VisibleLeaf).ToArray();
            if (people.Length > 0)
            {
                var named = new TreeViewItem
                {
                    Header = $"已命名人物（{counts?.GetValueOrDefault(ClassificationCountKeys.NamedPeople) ?? 0}）",
                    Tag = people.Select(x => x.Id).ToArray(), IsExpanded = true
                };
                foreach (var label in people)
                    named.Items.Add(new TreeViewItem { Header = Header(label), Tag = new[] { label.Id } });
                root.Items.Add(named);
            }
            foreach (var label in group.Where(x => !x.Id.StartsWith("person.confirmed.", StringComparison.Ordinal)).OrderBy(x => x.Name).Where(VisibleLeaf))
                root.Items.Add(new TreeViewItem { Header = Header(label), Tag = new[] { label.Id } });
            roots.Add(root);
        }
        // When nothing has been classified yet, show the full taxonomy with zero counts instead
        // of an empty tree that looks broken.
        if (roots.Count == 0 && counts is not null)
            foreach (var group in _taxonomy.Labels.Where(x => x.Active).GroupBy(x => x.Dimension).OrderBy(x => x.Key))
            {
                var groupIds = group.Where(x => x.Assignable && !x.Id.StartsWith("nav.", StringComparison.Ordinal))
                    .Select(x => x.Id).Distinct(StringComparer.Ordinal).ToArray();
                var root = new TreeViewItem { Header = $"{group.Key}（0）", IsExpanded = true, Tag = groupIds };
                foreach (var label in group.Where(x => !x.Id.StartsWith("person.confirmed.", StringComparison.Ordinal)).OrderBy(x => x.Name))
                    root.Items.Add(new TreeViewItem { Header = Header(label), Tag = new[] { label.Id } });
                roots.Add(root);
            }
        foreach (var root in roots) LabelTree.Items.Add(root);
        foreach (var item in TreeNodes())
            if (item.Tag is string[] ids && wasExpanded.TryGetValue(string.Join("|", ids), out var expanded)) item.IsExpanded = expanded;
        if (selectedKey is not null)
        {
            var selected = TreeNodes().FirstOrDefault(item => item.Tag is string[] ids && string.Join("|", ids) == selectedKey);
            if (selected is not null)
            {
                // Re-selecting raises SelectedItemChanged; the photo list is reloaded right after this.
                _restoringSelection = true;
                try { selected.IsSelected = true; }
                finally { _restoringSelection = false; }
            }
        }
        return;

        string Header(ClassificationLabel label) => $"{label.Name}（{counts?.GetValueOrDefault(label.Id) ?? 0}）";
    }

    private IEnumerable<TreeViewItem> TreeNodes() => LabelTree.Items.OfType<TreeViewItem>().SelectMany(SelfAndDescendants);
    private static IEnumerable<TreeViewItem> SelfAndDescendants(TreeViewItem node)
    {
        yield return node;
        foreach (var child in node.Items.OfType<TreeViewItem>())
            foreach (var descendant in SelfAndDescendants(child)) yield return descendant;
    }

    private async void LabelTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_restoringSelection) return;
        if (e.NewValue is TreeViewItem { Tag: string[] ids })
        {
            _selectedFilterIds = ids;
            _selectedLabelId = ids.Length == 1 ? ids[0] : null;
            // Selecting a node only changes which photos are listed; the tree stays exactly as it is.
            await RefreshAsync(rebuildTree: false);
        }
    }
    private async void FilterChanged(object sender, RoutedEventArgs e) { if (IsLoaded) await RefreshAsync(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void LoadMore_Click(object sender, RoutedEventArgs e) => await LoadMoreAsync();
    private async void Photos_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 3) await LoadMoreAsync();
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        try { var plan = await _client.Plan(_closed.Token); Feedback.Text = $"扫描计划：待处理 {plan.Pending}，未变化 {plan.Unchanged}"; await RefreshAsync(); }
        catch (Exception ex) { Feedback.Text = "扫描失败：" + ex.Message; }
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try { var plan = await _client.Plan(_closed.Token); var job = await _client.Start(plan.Id, _closed.Token); Feedback.Text = $"分类任务已启动：{job.Total} 张照片"; }
        catch (Exception ex) { Feedback.Text = "启动失败：" + ex.Message; }
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var row in Photos.Items.OfType<PhotoRow>()) row.Selected = true; }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) { foreach (var row in Photos.Items.OfType<PhotoRow>()) row.Selected = false; }
    private async void Decision_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _browse is null || _selectedLabelId is null) { Feedback.Text = "请先选择左侧分类和照片。"; return; }
        var selected = Photos.Items.OfType<PhotoRow>().Where(x => x.Selected).ToArray();
        if (selected.Length == 0) { Feedback.Text = "请先勾选照片。"; return; }
        try
        {
            var decision = new ClassificationDecision(Guid.NewGuid().ToString("N"), _browse.Snapshot, _selectedLabelId,
                button.Tag?.ToString() ?? "confirm", selected.ToDictionary(x => x.Record.ImageId, x => x.Record.ContentIdentity),
                selected.ToDictionary(x => x.Record.ImageId, x => x.Record.OwnerId));
            await _client.Decide(decision, _closed.Token); Feedback.Text = $"已提交 {selected.Length} 张照片的分类决定。"; await RefreshAsync();
        }
        catch (Exception ex) { Feedback.Text = "提交失败：" + ex.Message; }
    }
    private async void Photos_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Photos.SelectedItem is not PhotoRow row) return;
        try
        {
            if (AppServices.ServiceMode && AppServices.Gateway is HttpIndexGateway remote)
                await Services.RemotePhotoPreview.OpenAsync(remote, row.Record.ImageId, row.Record.Path, row.Record.ContentIdentity);
            else if (File.Exists(row.Record.Path))
            {
                using var stream = File.OpenRead(row.Record.Path); var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                new PhotoViewerWindow(bitmap, Path.GetFileName(row.Record.Path)) { Owner = this }.Show();
            }
        }
        catch (Exception ex) { Feedback.Text = "预览失败：" + ex.Message; }
    }

    private sealed class PhotoRow
    {
        public ClassificationPhoto Record { get; }
        public bool Selected { get; set; }
        public string Name => Path.GetFileName(Record.Path);
        public string Labels { get; }
        public ImageItemViewModel Preview { get; }
        public PhotoRow(ClassificationPhoto photo, ClassificationTaxonomySnapshot taxonomy, CancellationToken token)
        {
            Record = photo; Labels = string.Join("、", photo.LabelIds.Select(id => taxonomy.Labels.FirstOrDefault(x => x.Id == id)?.Name ?? id));
            Preview = new ImageItemViewModel(new ImageRecord { Id = photo.ImageId, Path = photo.Path, ThumbPath = photo.ThumbnailPath, ContentSha256 = photo.ContentIdentity }, token, AppServices.GetClientServices());
        }
    }
}
