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

    public ClassificationBrowserWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { _closed.Cancel(); _client.Dispose(); _closed.Dispose(); };
    }

    private async Task RefreshAsync()
    {
        try
        {
            var filter = new ClassificationFilter(
                string.IsNullOrWhiteSpace(_selectedLabelId)
                    ? Array.Empty<ClassificationFilterGroup>()
                    : new[] { new ClassificationFilterGroup(ClassificationMatchMode.Any, new[] { _selectedLabelId! }) },
                Array.Empty<string>());
            _browse = await _client.Browse(new ClassificationBrowseRequest(filter,
                IncludeSuggestions.IsChecked == true, IncludeLegacy.IsChecked == true), _closed.Token);
            _taxonomy = _browse.Taxonomy;
            BuildLabelTree();
            var rows = _browse.Photos.Select(photo => new PhotoRow(photo, _taxonomy, _closed.Token)).ToArray();
            Photos.ItemsSource = new ObservableCollection<PhotoRow>(rows);
            StatusText.Text = $"{(_selectedLabelId is null ? "全部分类" : _taxonomy.Labels.FirstOrDefault(x => x.Id == _selectedLabelId)?.Name ?? _selectedLabelId)}：{rows.Length} 张照片";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Feedback.Text = "分类读取失败：" + ex.Message; }
    }

    private void BuildLabelTree()
    {
        if (_taxonomy is null) return;
        LabelTree.Items.Clear();
        foreach (var group in _taxonomy.Labels.Where(x => x.Active).GroupBy(x => x.Dimension).OrderBy(x => x.Key))
        {
            var root = new TreeViewItem { Header = group.Key, IsExpanded = true, Tag = null };
            foreach (var label in group.OrderBy(x => x.Name)) root.Items.Add(new TreeViewItem { Header = label.Name, Tag = label.Id });
            LabelTree.Items.Add(root);
        }
    }

    private async void LabelTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: string id }) { _selectedLabelId = id; await RefreshAsync(); }
    }
    private async void FilterChanged(object sender, RoutedEventArgs e) { if (IsLoaded) await RefreshAsync(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
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
