using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Picer.App.ViewModels;
using Picer.Core.Logging;

namespace Picer.App.Controls;

public partial class PhotoGrid : UserControl
{
    private void ContentReferences_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is ImageItemViewModel { IsVideo: false } item && (item.Record.ContentSha256 ?? item.Record.Sha256) is { Length: 64 } hash)
            new Views.ContentReferencesWindow(hash) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
    private async void PurgeMissing_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: ImageItemViewModel item } button || !item.IsMissing || item.IsVideo) return;
        if (MessageBox.Show(Window.GetWindow(this), "彻底删除此位置的索引记录和关联缓存？若仍有相同文件的其他位置，共享内容索引将继续保留；最后一个位置删除后才会清除共享描述、分析和缩略图。",
                "彻底删除此位置记录", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        button.IsEnabled = false;
        try
        {
            await AppServices.PurgeMissingImageAsync(item.Record);
            var source = ItemsSource is System.ComponentModel.ICollectionView view ? view.SourceCollection : ItemsSource;
            if (source is IList list && !list.IsReadOnly) list.Remove(item);
        }
        catch (Exception exception) { MessageBox.Show(Window.GetWindow(this), "删除未完成，可重试：" + exception.Message); }
        finally { button.IsEnabled = true; }
    }
    private Point _dragStartPoint;
    private int _dragStartIndex = -1;
    private bool _dragSelecting;
    private HashSet<object> _selectionBeforeDrag = [];
    private Task? _prefetchWorker;
    private readonly HashSet<ImageItemViewModel> _prefetchItems = [];
    private long _lastPrefetchRequestTicks;
    private long _lastPrefetchLogTicks;
    private long _lastPrefetchBatchLogTicks;
    private INotifyCollectionChanged? _itemsNotifier;
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(PhotoGrid), new PropertyMetadata(null, OnItemsSourceChanged));
    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(PhotoGrid), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty SelectedItemsTargetProperty = DependencyProperty.Register(nameof(SelectedItemsTarget), typeof(IList), typeof(PhotoGrid));
    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(PhotoGrid));
    public static readonly DependencyProperty ShowInExplorerCommandProperty = DependencyProperty.Register(nameof(ShowInExplorerCommand), typeof(ICommand), typeof(PhotoGrid));
    public static readonly DependencyProperty ShowDetailsCommandProperty = DependencyProperty.Register(nameof(ShowDetailsCommand), typeof(ICommand), typeof(PhotoGrid));
    public static readonly DependencyProperty EditLocationCommandProperty = DependencyProperty.Register(nameof(EditLocationCommand), typeof(ICommand), typeof(PhotoGrid));
    public static readonly DependencyProperty EditTagsCommandProperty = DependencyProperty.Register(nameof(EditTagsCommand), typeof(ICommand), typeof(PhotoGrid));
    public static readonly DependencyProperty ThumbnailSizeProperty = DependencyProperty.Register(nameof(ThumbnailSize), typeof(double), typeof(PhotoGrid), new PropertyMetadata(150d, OnSizeChanged));
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(PhotoGrid), new PropertyMetadata(170d));
    public static readonly DependencyProperty CardHeightProperty = DependencyProperty.Register(nameof(CardHeight), typeof(double), typeof(PhotoGrid), new PropertyMetadata(205d));
    public static readonly DependencyProperty SelectedInfoTextProperty = DependencyProperty.Register(nameof(SelectedInfoText), typeof(string), typeof(PhotoGrid), new PropertyMetadata("未选择照片"));
    public static readonly DependencyProperty TimelineModeProperty = DependencyProperty.Register(nameof(TimelineMode), typeof(bool), typeof(PhotoGrid), new PropertyMetadata(false));
    public static readonly DependencyProperty ScrollToItemProperty = DependencyProperty.Register(nameof(ScrollToItem), typeof(object), typeof(PhotoGrid), new PropertyMetadata(null, OnScrollToItemChanged));

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public IList? SelectedItemsTarget { get => (IList?)GetValue(SelectedItemsTargetProperty); set => SetValue(SelectedItemsTargetProperty, value); }
    public ICommand? OpenCommand { get => (ICommand?)GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }
    public ICommand? ShowInExplorerCommand { get => (ICommand?)GetValue(ShowInExplorerCommandProperty); set => SetValue(ShowInExplorerCommandProperty, value); }
    public ICommand? ShowDetailsCommand { get => (ICommand?)GetValue(ShowDetailsCommandProperty); set => SetValue(ShowDetailsCommandProperty, value); }
    public ICommand? EditLocationCommand { get => (ICommand?)GetValue(EditLocationCommandProperty); set => SetValue(EditLocationCommandProperty, value); }
    public ICommand? EditTagsCommand { get => (ICommand?)GetValue(EditTagsCommandProperty); set => SetValue(EditTagsCommandProperty, value); }
    public double ThumbnailSize { get => (double)GetValue(ThumbnailSizeProperty); set => SetValue(ThumbnailSizeProperty, value); }
    public double CardWidth { get => (double)GetValue(CardWidthProperty); private set => SetValue(CardWidthProperty, value); }
    public double CardHeight { get => (double)GetValue(CardHeightProperty); private set => SetValue(CardHeightProperty, value); }
    public string SelectedInfoText { get => (string)GetValue(SelectedInfoTextProperty); set => SetValue(SelectedInfoTextProperty, value); }
    public bool TimelineMode { get => (bool)GetValue(TimelineModeProperty); set => SetValue(TimelineModeProperty, value); }
    public object? ScrollToItem { get => GetValue(ScrollToItemProperty); set => SetValue(ScrollToItemProperty, value); }

    public PhotoGrid()
    {
        InitializeComponent();
        Unloaded += PhotoGrid_Unloaded;
        Loaded += PhotoGrid_Loaded;
    }
    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (PhotoGrid)d;
        if (grid._itemsNotifier is not null) grid._itemsNotifier.CollectionChanged -= grid.Items_CollectionChanged;
        grid._itemsNotifier = e.NewValue as INotifyCollectionChanged;
        if (grid._itemsNotifier is not null) grid._itemsNotifier.CollectionChanged += grid.Items_CollectionChanged;
        grid.ScheduleGreedyPrefetch();
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleGreedyPrefetch();

    private void ScheduleGreedyPrefetch()
    {
        if (!AppServices.ServiceMode) return;
        var viewport = FindViewportRange();
        var visibleNow = new HashSet<ImageItemViewModel>();
        if (viewport.First >= 0)
            for (var i = viewport.First; i <= viewport.Last && i < List.Items.Count; i++)
                if (List.Items[i] is ImageItemViewModel item) { visibleNow.Add(item); item.EnsureThumbnailLoad(); }
        foreach (var item in _prefetchItems.ToArray())
        {
            if (!visibleNow.Contains(item)) item.CancelThumbnailPrefetch();
            if (!item.IsThumbnailLoading || visibleNow.Contains(item)) _prefetchItems.Remove(item);
        }
        Volatile.Write(ref _lastPrefetchRequestTicks, DateTime.UtcNow.Ticks);
        var now = DateTime.UtcNow.Ticks;
        if (now - Volatile.Read(ref _lastPrefetchLogTicks) > TimeSpan.TicksPerSecond)
        {
            Volatile.Write(ref _lastPrefetchLogTicks, now);
            AppLogger.Info($"Thumbnail prefetch scheduled; items={List.Items.Count}; visible={IsVisible}");
        }
        if (_prefetchWorker is null || _prefetchWorker.IsCompleted)
            _prefetchWorker = PrefetchWhenIdleAsync();
    }

    private async Task PrefetchWhenIdleAsync()
    {
        try
        {
            await Task.Delay(750);
            while (AppServices.ServiceMode && IsVisible)
            {
                var idleDeadline = Volatile.Read(ref _lastPrefetchRequestTicks) + TimeSpan.TicksPerMillisecond * 750;
                var remaining = idleDeadline - DateTime.UtcNow.Ticks;
                if (remaining > 0)
                {
                    await Task.Delay(TimeSpan.FromTicks(remaining));
                    continue;
                }
                var all = List.Items.OfType<ImageItemViewModel>().ToArray();
                var visible = new HashSet<ImageItemViewModel>();
                var range = FindViewportRange();
                if (range.First >= 0)
                    for (var index = range.First; index <= range.Last && index < List.Items.Count; index++)
                        if (List.Items[index] is ImageItemViewModel item) visible.Add(item);
                var span = Math.Clamp(range.Last - range.First + 1, 8, 128);
                var near = all.Skip(Math.Max(0, range.First - span)).Take(span * 3);
                var ordered = visible.Concat(near.Where(item => !visible.Contains(item))).ToArray();
                var available = Math.Max(0, 8 - ordered.Count(item => item.IsThumbnailLoading));
                var pending = ordered.Where(item => !item.HasThumbnail && !item.IsThumbnailLoading).Take(available).ToArray();
                var batchNow = DateTime.UtcNow.Ticks;
                if (batchNow - Volatile.Read(ref _lastPrefetchBatchLogTicks) > TimeSpan.TicksPerSecond)
                {
                    Volatile.Write(ref _lastPrefetchBatchLogTicks, batchNow);
                    AppLogger.Info($"Thumbnail prefetch batch; items={all.Length}; viewport={range.First}-{range.Last}; pending={pending.Length}");
                }
                if (pending.Length == 0)
                {
                    if (ordered.Any(item => item.IsThumbnailLoading)) { await Task.Delay(80); continue; }
                    return;
                }
                foreach (var item in pending)
                {
                    if (visible.Contains(item)) item.EnsureThumbnailLoad();
                    else { _prefetchItems.Add(item); item.PrefetchThumbnail(); }
                }
                await Task.Delay(40);
            }
        }
        catch (OperationCanceledException) { }
    }
    private static void OnScrollToItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PhotoGrid grid || e.NewValue is null) return;
        grid.Dispatcher.BeginInvoke(() => grid.List.ScrollIntoView(e.NewValue), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (PhotoGrid)d;
        grid.CardWidth = (double)e.NewValue + 20;
        grid.CardHeight = (double)e.NewValue + 58;
    }
    private void Execute(ICommand? command) { if (command?.CanExecute(null) == true) command.Execute(null); }
    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Execute(OpenCommand);
    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Execute(OpenCommand); e.Handled = true; }
    }
    private void Open_Click(object sender, RoutedEventArgs e) => Execute(OpenCommand);
    private void Explorer_Click(object sender, RoutedEventArgs e) => Execute(ShowInExplorerCommand);
    private void Details_Click(object sender, RoutedEventArgs e) => Execute(ShowDetailsCommand);
    private void EditLocation_Click(object sender, RoutedEventArgs e) => Execute(EditLocationCommand);
    private void EditTags_Click(object sender, RoutedEventArgs e) => Execute(EditTagsCommand);
    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var video = SelectedItem is ImageItemViewModel { IsVideo: true };
        DetailsMenuItem.Header = video ? "查看视频信息" : "查看图片信息";
        OpenMenuItem.Header = video ? "播放视频" : "打开图片";
        EditLocationMenuItem.IsEnabled = !video;
        EditTagsMenuItem.IsEnabled = !video;
        EditLocationMenuItem.ToolTip = video ? "视频地点编辑将在视频元数据索引中提供" : null;
        EditTagsMenuItem.ToolTip = video ? "视频标签将在视频元数据索引中提供" : null;
    }
    private void BurstMember_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ImageItemViewModel image) return;
        SelectedItem = image;
    }
    private void BurstMember_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        BurstMember_Click(sender, e);
        Execute(OpenCommand);
        e.Handled = true;
    }
    private void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox) return;
        DependencyObject? element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListBoxItem)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        if (element is ListBoxItem item && !item.IsSelected)
        {
            ((ListBox)sender).SelectedItems.Clear();
            item.IsSelected = true;
        }
    }
    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || FindContainer(e.OriginalSource as DependencyObject) is not { } item ||
            item.DataContext is not ImageItemViewModel) return;
        _dragStartPoint = e.GetPosition(list);
        _dragStartIndex = list.ItemContainerGenerator.IndexFromContainer(item);
        _dragSelecting = false;
        _selectionBeforeDrag = list.SelectedItems.Cast<object>().ToHashSet();
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox list || e.LeftButton != MouseButtonState.Pressed || _dragStartIndex < 0) return;
        var point = e.GetPosition(list);
        if (!_dragSelecting && Math.Abs(point.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragSelecting = true;
        if (FindContainer(e.OriginalSource as DependencyObject) is not { } item || item.DataContext is not ImageItemViewModel) return;
        var currentIndex = list.ItemContainerGenerator.IndexFromContainer(item);
        if (currentIndex < 0) return;
        var additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        list.SelectedItems.Clear();
        if (additive)
            foreach (var selected in _selectionBeforeDrag.Where(list.Items.Contains)) list.SelectedItems.Add(selected);
        var first = Math.Min(_dragStartIndex, currentIndex);
        var last = Math.Max(_dragStartIndex, currentIndex);
        for (var index = first; index <= last; index++)
            if (list.Items[index] is ImageItemViewModel image && !list.SelectedItems.Contains(image))
                list.SelectedItems.Add(image);
        e.Handled = true;
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragStartIndex = -1;
        _dragSelecting = false;
        _selectionBeforeDrag.Clear();
    }

    private static ListBoxItem? FindContainer(DependencyObject? element)
    {
        while (element is not null && element is not ListBoxItem)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        return element as ListBoxItem;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedItemsTarget is null || sender is not ListBox list) return;
        SelectedItemsTarget.Clear();
        foreach (var item in list.SelectedItems.OfType<ImageItemViewModel>()) SelectedItemsTarget.Add(item);
    }
    private void PhotoGrid_Unloaded(object sender, RoutedEventArgs e)
    {
        Volatile.Write(ref _lastPrefetchRequestTicks, 0);
        foreach (var item in _prefetchItems) item.CancelThumbnailPrefetch();
        _prefetchItems.Clear();
    }

    private void PhotoGrid_Loaded(object sender, RoutedEventArgs e) => ScheduleGreedyPrefetch();

    private void PhotoGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ScheduleGreedyPrefetch();

    private void List_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) > 0.01 || Math.Abs(e.HorizontalChange) > 0.01)
        {
            var now = DateTime.UtcNow.Ticks;
            if (now - Volatile.Read(ref _lastPrefetchLogTicks) > TimeSpan.TicksPerSecond)
            {
                Volatile.Write(ref _lastPrefetchLogTicks, now);
                AppLogger.Info($"Thumbnail scroll; offset={e.VerticalOffset:F0}; viewport={e.ViewportHeight:F0}; extent={e.ExtentHeight:F0}");
            }
            ScheduleGreedyPrefetch();
        }
    }

    private (int First, int Last) FindViewportRange()
    {
        var panel = FindVisualChild<VirtualizingWrapPanel>(List);
        return panel?.GetViewportItemRange() ?? (-1, -1);
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

    private void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ImageItemViewModel image }) return;
        image.EnsureThumbnailLoad();
        // Remote thumbnails are persisted by content identity.  Warm roughly
        // one viewport ahead without affecting the UI thread; already-loaded
        // and in-flight items are no-ops, so scrolling cannot duplicate work.
    }
}
