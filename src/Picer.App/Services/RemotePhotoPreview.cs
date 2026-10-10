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
            await using var input = await gateway.OpenPreviewAsync(id, stop.Token);
            if (input is null) status.Text = "服务暂不提供预览，可以下载原图。";
            else
            {
                using var bytes = new MemoryStream();
                var buffer = new byte[64 * 1024];
                int count;
                while ((count = await input.ReadAsync(buffer, stop.Token)) > 0)
                {
                    if (bytes.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("preview_too_large");
                    bytes.Write(buffer, 0, count);
                }
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
