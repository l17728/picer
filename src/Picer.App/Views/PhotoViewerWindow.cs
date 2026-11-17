using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation;

namespace Picer.App.Views;

public sealed class PhotoViewerWindow : Window
{
    private readonly Image _image;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly BitmapSource _source;
    private double _scale;
    private bool _fit = true;

    public PhotoViewerWindow(BitmapSource source, string title)
    {
        _source = source;
        Title = title; Width = 1000; Height = 760; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "ClassificationPhotoViewer");
        var panel = new DockPanel();
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        void Button(string text, Action action)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
            button.Click += (_, _) => action(); toolbar.Children.Add(button);
        }
        Button("完整显示", () => { _fit = true; Fit(); });
        Button("原始大小", () => Zoom(1));
        Button("缩小", () => Zoom(_scale / 1.2));
        Button("放大", () => Zoom(_scale * 1.2));
        toolbar.Children.Add(_status);
        DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar);
        _image = new Image { Source = source, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(_image, "ClassificationFullPhoto");
        _scroll = new ScrollViewer { Content = _image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Black,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        panel.Children.Add(_scroll); Content = panel;
        Loaded += (_, _) => Fit();
        _scroll.SizeChanged += (_, _) => { if (_fit) Fit(); };
        _scroll.PreviewMouseWheel += (_, e) => { Zoom(_scale * (e.Delta > 0 ? 1.2 : 1 / 1.2)); e.Handled = true; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void Fit()
    {
        if (_scroll.ActualWidth <= 2 || _scroll.ActualHeight <= 2) return;
        ApplyScale(Math.Min((_scroll.ActualWidth - 2) / _source.Width, (_scroll.ActualHeight - 2) / _source.Height));
        _scroll.ScrollToHorizontalOffset(0); _scroll.ScrollToVerticalOffset(0);
    }

    private void Zoom(double scale)
    {
        _fit = false;
        ApplyScale(Math.Clamp(scale, 0.01, 20));
    }

    private void ApplyScale(double scale)
    {
        _scale = scale; _image.Width = _source.Width * scale; _image.Height = _source.Height * scale;
        _status.Text = $"{scale:P0} · 滚轮缩放；放大后可用滚动条浏览";
    }
}
