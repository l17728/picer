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
            if (rebuildTree && BuildLabelTree()) _browse = await BrowsePage(null);
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

    private bool BuildLabelTree()
    {
        if (_taxonomy is null) return false;
        // A rebuild must never undo what the user did to the tree: remember which nodes were
        // expanded or collapsed and which one was selected, then put that state back.
        var wasExpanded = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in TreeNodes())
            if (item.Tag is TreeNodeSelection selection) wasExpanded[selection.Key] = item.IsExpanded;
        var selectedKey = LabelTree.SelectedItem is TreeViewItem { Tag: TreeNodeSelection selectedTag } ? selectedTag.Key : null;
        LabelTree.Items.Clear();
        var counts = _browse?.Counts;
        var graph = new ClassificationTaxonomy(_taxonomy.Labels, _taxonomy.Edges);
        var labels = _taxonomy.Labels.Where(label => label.Active).ToDictionary(label => label.Id, StringComparer.Ordinal);
        var children = labels.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var parents = labels.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in _taxonomy.Edges.Where(edge => edge.Kind != TaxonomyEdgeKind.Related))
        {
            if (!labels.ContainsKey(edge.ParentId) || !labels.ContainsKey(edge.ChildId)) continue;
            children[edge.ParentId].Add(edge.ChildId);
            parents[edge.ChildId].Add(edge.ParentId);
        }
        bool HasPhotos(string id) => counts is null || counts.GetValueOrDefault(id) > 0;
        string[] MatchingIds(string id) => graph.Expand(id).Where(child => labels.TryGetValue(child, out var label) && label.Assignable)
            .Order(StringComparer.Ordinal).ToArray();
        var roots = new List<TreeViewItem>();
        foreach (var group in labels.Values.GroupBy(label => label.Dimension).OrderBy(group => group.Key))
        {
            var navigationRootId = "nav." + group.Key;
            var hasNavigationRoot = labels.ContainsKey(navigationRootId);
            var groupIds = hasNavigationRoot
                ? MatchingIds(navigationRootId).Where(HasPhotos).ToArray()
                : group.Where(label => label.Assignable && HasPhotos(label.Id)).Select(label => label.Id)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var dimensionCount = hasNavigationRoot
                ? counts?.GetValueOrDefault(navigationRootId) ?? 0
                : counts?.GetValueOrDefault(ClassificationCountKeys.Dimension(group.Key)) ?? 0;
            if (dimensionCount == 0 || groupIds.Length == 0) continue;
            var root = new TreeViewItem
            {
                Header = $"{DimensionName(group.Key)}（{dimensionCount}）", IsExpanded = true,
                Tag = new TreeNodeSelection("dimension:" + group.Key, groupIds, null)
            };
            // The preset's nav.<dimension> root defines the smart-album tree. Cross-dimension
            // Browse edges intentionally make a place (for example a park) visible under both
            // Place and Outdoor; each node's count is the distinct photo union of its subtree.
            if (hasNavigationRoot && HasPhotos(navigationRootId))
                root.Items.Add(CreateNode(labels[navigationRootId], new HashSet<string>(StringComparer.Ordinal)));
            else
            {
                var rootsInDimension = group.Where(label => HasPhotos(label.Id)
                    && !parents[label.Id].Any(parent => labels.TryGetValue(parent, out var parentLabel)
                        && parentLabel.Dimension == group.Key && HasPhotos(parent)))
                    .OrderBy(label => label.Name, StringComparer.Ordinal).ThenBy(label => label.Id, StringComparer.Ordinal);
                foreach (var label in rootsInDimension) root.Items.Add(CreateNode(label, new HashSet<string>(StringComparer.Ordinal)));
            }
            roots.Add(root);
        }
        foreach (var root in roots) LabelTree.Items.Add(root);
        foreach (var item in TreeNodes())
            if (item.Tag is TreeNodeSelection selection && wasExpanded.TryGetValue(selection.Key, out var expanded)) item.IsExpanded = expanded;
        var selectionReset = false;
        if (selectedKey is not null)
        {
            var selected = TreeNodes().FirstOrDefault(item => item.Tag is TreeNodeSelection selection && selection.Key == selectedKey);
            if (selected is not null)
            {
                // Re-selecting raises SelectedItemChanged; the photo list is reloaded right after this.
                _restoringSelection = true;
                try { selected.IsSelected = true; }
                finally { _restoringSelection = false; }
            }
            else
            {
                _selectedLabelId = null;
                _selectedFilterIds = Array.Empty<string>();
                selectionReset = true;
            }
        }
        SetDecisionButtons(_selectedLabelId is not null);
        return selectionReset;

        TreeViewItem CreateNode(ClassificationLabel label, HashSet<string> ancestors)
        {
            if (!ancestors.Add(label.Id)) return new TreeViewItem { Header = $"{label.Name}（{counts?.GetValueOrDefault(label.Id) ?? 0}）" };
            var matchingIds = MatchingIds(label.Id);
            var node = new TreeViewItem
            {
                Header = $"{label.Name}（{counts?.GetValueOrDefault(label.Id) ?? 0}）",
                Tag = new TreeNodeSelection(label.Id, matchingIds, label.Assignable ? label.Id : null)
            };
            foreach (var child in children[label.Id].Distinct(StringComparer.Ordinal)
                .Where(child => labels.TryGetValue(child, out var childLabel) && HasPhotos(childLabel.Id))
                .Select(child => labels[child]).OrderBy(child => child.Name, StringComparer.Ordinal).ThenBy(child => child.Id, StringComparer.Ordinal))
                node.Items.Add(CreateNode(child, new HashSet<string>(ancestors, StringComparer.Ordinal)));
            return node;
        }
    }

    private static string DimensionName(string dimension) => dimension switch
    {
        "subject" => "人物与主体", "nature" => "自然与风景", "place" => "地点与空间",
        "architecture" => "城市与建筑", "activity" => "活动", "event" => "事件与节日",
        "object" => "物品与食物", "environment" => "环境与光线", "document" => "文档资料",
        _ => dimension
    };

    private void SetDecisionButtons(bool enabled)
    {
        ConfirmClassificationButton.IsEnabled = enabled;
        RejectClassificationButton.IsEnabled = enabled;
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
        if (e.NewValue is TreeViewItem { Tag: TreeNodeSelection selection })
        {
            _selectedFilterIds = selection.FilterIds;
            _selectedLabelId = selection.DecisionLabelId;
            SetDecisionButtons(_selectedLabelId is not null);
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

    private sealed record TreeNodeSelection(string Key, string[] FilterIds, string? DecisionLabelId);
}
