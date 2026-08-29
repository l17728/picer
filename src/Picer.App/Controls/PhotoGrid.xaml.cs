using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Picer.App.ViewModels;

namespace Picer.App.Controls;

public partial class PhotoGrid : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(PhotoGrid));
    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(PhotoGrid), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty SelectedItemsTargetProperty = DependencyProperty.Register(nameof(SelectedItemsTarget), typeof(IList), typeof(PhotoGrid));
    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(PhotoGrid));
    public static readonly DependencyProperty ShowInExplorerCommandProperty = DependencyProperty.Register(nameof(ShowInExplorerCommand), typeof(ICommand), typeof(PhotoGrid));
    public static readonly DependencyProperty ShowDetailsCommandProperty = DependencyProperty.Register(nameof(ShowDetailsCommand), typeof(ICommand), typeof(PhotoGrid));
    public static readonly DependencyProperty ThumbnailSizeProperty = DependencyProperty.Register(nameof(ThumbnailSize), typeof(double), typeof(PhotoGrid), new PropertyMetadata(150d, OnSizeChanged));
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(PhotoGrid), new PropertyMetadata(170d));
    public static readonly DependencyProperty CardHeightProperty = DependencyProperty.Register(nameof(CardHeight), typeof(double), typeof(PhotoGrid), new PropertyMetadata(205d));
    public static readonly DependencyProperty SelectedInfoTextProperty = DependencyProperty.Register(nameof(SelectedInfoText), typeof(string), typeof(PhotoGrid), new PropertyMetadata("未选择照片"));

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public IList? SelectedItemsTarget { get => (IList?)GetValue(SelectedItemsTargetProperty); set => SetValue(SelectedItemsTargetProperty, value); }
    public ICommand? OpenCommand { get => (ICommand?)GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }
    public ICommand? ShowInExplorerCommand { get => (ICommand?)GetValue(ShowInExplorerCommandProperty); set => SetValue(ShowInExplorerCommandProperty, value); }
    public ICommand? ShowDetailsCommand { get => (ICommand?)GetValue(ShowDetailsCommandProperty); set => SetValue(ShowDetailsCommandProperty, value); }
    public double ThumbnailSize { get => (double)GetValue(ThumbnailSizeProperty); set => SetValue(ThumbnailSizeProperty, value); }
    public double CardWidth { get => (double)GetValue(CardWidthProperty); private set => SetValue(CardWidthProperty, value); }
    public double CardHeight { get => (double)GetValue(CardHeightProperty); private set => SetValue(CardHeightProperty, value); }
    public string SelectedInfoText { get => (string)GetValue(SelectedInfoTextProperty); set => SetValue(SelectedInfoTextProperty, value); }

    public PhotoGrid() => InitializeComponent();
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
    private void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox) return;
        DependencyObject? element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListBoxItem)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        if (element is ListBoxItem item) item.IsSelected = true;
    }
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedItemsTarget is null || sender is not ListBox list) return;
        SelectedItemsTarget.Clear();
        foreach (var item in list.SelectedItems) SelectedItemsTarget.Add(item);
    }
    private void Thumbnail_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ImageItemViewModel image }) image.CancelThumbnailLoad();
    }
}
