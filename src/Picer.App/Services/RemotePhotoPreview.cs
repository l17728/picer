using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Picer.Core.Gateway;
using Picer.Core.Logging;

namespace Picer.App.Services;

public static class RemotePhotoPreview
{
    public static async Task OpenAsync(HttpIndexGateway gateway, long id, string path, string? hash)
    {
        using var stop = new CancellationTokenSource();
        var image = new Image { Stretch = Stretch.Uniform };
        var status = new TextBlock { Text = "正在读取预览…", Margin = new Thickness(8) };
        var original = new Button { Content = "下载并打开原图", Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new DockPanel();
        DockPanel.SetDock(status, Dock.Top); panel.Children.Add(status);
        DockPanel.SetDock(original, Dock.Bottom); panel.Children.Add(original); panel.Children.Add(image);
        var window = new Window { Title = "照片预览（最长边 1600 像素）", Width = 960, Height = 720, Content = panel, Owner = Application.Current.MainWindow };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => { stop.Cancel(); image.Source = null; closed.TrySetResult(); };
        original.Click += async (_, _) => { original.IsEnabled = false; try { await MediaPlayback.OpenAsync(gateway, id, false, path, hash); } finally { original.IsEnabled = true; } };
        window.Show();
        try
        {
            var cached = await SharedImageAssets.GetAsync(gateway, id, path, hash, true, stop.Token);
            using (var bytes = File.OpenRead(cached))
            {
                var timer = Stopwatch.StartNew();
                bytes.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1600; bitmap.StreamSource = bytes; bitmap.EndInit(); bitmap.Freeze();
                stop.Token.ThrowIfCancellationRequested();
                image.Source = bitmap; status.Text = "预览已加载；查看原始分辨率请下载原图。";
                AppLogger.Info($"Media preview decoded; bytes={bytes.Length}; decodeMs={timer.ElapsedMilliseconds}");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception exception) { status.Text = "预览读取失败，可以下载原图。错误类型：" + exception.GetType().Name; }
        await closed.Task;
    }
}
