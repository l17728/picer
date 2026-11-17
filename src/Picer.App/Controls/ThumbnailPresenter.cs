using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Picer.App.ViewModels;

namespace Picer.App.Controls;

/// <summary>Adapts an image to the same cache, scheduler, decoder and retry policy as PhotoGrid.</summary>
public static class ThumbnailPresenter
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path", typeof(string), typeof(ThumbnailPresenter), new PropertyMetadata(null, PathChanged));
    public static void SetPath(DependencyObject target, string? value) => target.SetValue(PathProperty, value);
    public static string? GetPath(DependencyObject target) => (string?)target.GetValue(PathProperty);
    private static void PathChanged(DependencyObject target, DependencyPropertyChangedEventArgs args) => SetItem(target,
        args.NewValue is string { Length: > 0 } path ? new ImageItemViewModel(new Picer.Core.Models.ImageRecord { ThumbPath = path }) : null);
    public static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached(
        "Item", typeof(ImageItemViewModel), typeof(ThumbnailPresenter), new PropertyMetadata(null, Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(Presentation), typeof(ThumbnailPresenter));
    public static void SetItem(DependencyObject target, ImageItemViewModel? value) => target.SetValue(ItemProperty, value);
    public static ImageItemViewModel? GetItem(DependencyObject target) => (ImageItemViewModel?)target.GetValue(ItemProperty);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Image image) return;
        if (image.GetValue(StateProperty) is Presentation previous) previous.Dispose();
        image.Source = null;
        image.SetValue(StateProperty, args.NewValue is ImageItemViewModel item ? new Presentation(image, item) : null);
    }

    private sealed class Presentation : IDisposable
    {
        private readonly Image _image;
        private readonly ImageItemViewModel _item;
        private readonly DispatcherTimer _retry;
        private bool _subscribed;
        public Presentation(Image image, ImageItemViewModel item)
        {
            _image = image;
            _item = item;
            _retry = new DispatcherTimer(DispatcherPriority.Background, image.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _retry.Tick += Tick;
            image.Loaded += Loaded;
            image.Unloaded += Unloaded;
            image.IsVisibleChanged += VisibilityChanged;
            Update();
        }
        private void Loaded(object sender, RoutedEventArgs args) => Update();
        private void Unloaded(object sender, RoutedEventArgs args) => Stop();
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Update();
        private void Tick(object? sender, EventArgs args) => Update();
        private void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ImageItemViewModel.Thumbnail)) Update();
        }
        private void Update()
        {
            if (!_image.IsLoaded || !_image.IsVisible) { Stop(); return; }
            if (!_subscribed) { _item.PropertyChanged += Changed; _subscribed = true; }
            if (_item.HasThumbnail) { _image.Source = _item.Thumbnail; _retry.Stop(); }
            else { _item.EnsureThumbnailLoad(); _retry.Start(); }
        }
        private void Stop()
        {
            _retry.Stop();
            if (_subscribed) { _item.PropertyChanged -= Changed; _subscribed = false; }
            _item.PauseThumbnailLoad();
        }
        public void Dispose()
        {
            Stop();
            _retry.Tick -= Tick;
            _image.Loaded -= Loaded;
            _image.Unloaded -= Unloaded;
            _image.IsVisibleChanged -= VisibilityChanged;
        }
    }
}
