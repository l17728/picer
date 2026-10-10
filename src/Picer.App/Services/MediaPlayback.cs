using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Picer.App.Infrastructure;
using Picer.App.Views;
using Picer.Core.Gateway;

namespace Picer.App.Services;

public static class MediaPlayback
{
    private static readonly Lazy<PlayableMediaCache> Cache = new(() => new(Path.Combine(AppPaths.DataRoot, "remote-assets", "playable")));
    private static readonly CancellationTokenSource ShutdownToken = new();
    private static CancellationTokenSource SessionsStop = new();
    private static readonly HashSet<Task> Sessions = [];
    private static readonly object SessionGate = new();
    private static int _stopping;
    public static void Shutdown() => ShutdownToken.Cancel();
    public static async Task StopAllAsync()
    {
        CancellationTokenSource previous;
        Task[] pending;
        lock (SessionGate)
        {
            _stopping++;
            previous = SessionsStop;
            SessionsStop = new();
            pending = Sessions.ToArray();
        }
        try { await previous.CancelAsync(); await Task.WhenAll(pending); }
        finally { previous.Dispose(); lock (SessionGate) _stopping--; }
    }

    public static void Configure()
    { new VideoPlayerWindow { Owner = Application.Current.MainWindow }.ShowDialog(); }

    public static Task OpenLocalAsync(string path)
    {
        var settings = VideoPlayerSettings.Load();
        if (!VideoPlayerSettings.IsConfigured || settings.AskEveryTime)
        {
            var choose = new VideoPlayerWindow { Owner = Application.Current.MainWindow };
            if (choose.ShowDialog() != true) return Task.CompletedTask;
            settings = choose.Selection!;
        }
        var executable = settings.ResolveExecutable(Path.GetExtension(path));
        try { Launch(executable, path, executable.Length > 0 ? VideoPlayerSettings.Identify(executable) : VideoPlayerKind.Unknown)?.Dispose(); }
        catch (Exception exception) { MessageBox.Show("无法打开播放器：" + exception.GetType().Name, "播放失败"); }
        return Task.CompletedTask;
    }

    public static async Task OpenAsync(IMediaAssetGateway gateway, long id, bool video, string originalPath, string? hash = null)
    {
        var settings = VideoPlayerSettings.Load();
        if (video && (!VideoPlayerSettings.IsConfigured || settings.AskEveryTime))
        {
            var choose = new VideoPlayerWindow { Owner = Application.Current.MainWindow };
            if (choose.ShowDialog() != true) return;
            settings = choose.Selection!;
        }
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource stop;
        lock (SessionGate)
        {
            if (_stopping > 0 || Sessions.Count >= 4) return;
            stop = CancellationTokenSource.CreateLinkedTokenSource(ShutdownToken.Token, SessionsStop.Token);
            Sessions.Add(finished.Task);
        }
        using var stopLifetime = stop;
        VideoPlaybackProxy? proxy = null;
        ParallelMediaReader? reader = null;
        Process? process = null;
        Window? window = null;
        try
        {
        var panel = new StackPanel { Margin = new Thickness(20) };
        var status = new TextBlock { Text = "正在检查读取方式…", TextWrapping = TextWrapping.Wrap }; panel.Children.Add(status);
        var fallback = new Button { Content = "改为下载后播放", IsEnabled = false, Margin = new Thickness(0, 14, 0, 8) }; panel.Children.Add(fallback);
        var cancel = new Button { Content = "取消 / 关闭播放连接" }; panel.Children.Add(cancel);
        window = new Window { Title = video ? "视频播放" : "读取照片", Content = panel, Width = 420, Height = 200, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var downloadRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fallback.Click += (_, _) => downloadRequested.TrySetResult();
        cancel.Click += (_, _) => window.Close();
        window.Closed += (_, _) => stop.Cancel();
        window.Show();
            var extension = Path.GetExtension(originalPath);
            var executable = video ? settings.ResolveExecutable(extension) : "";
            var kind = executable.Length > 0 ? VideoPlayerSettings.Identify(executable) : VideoPlayerKind.Unknown;
            RemoteMediaInfo? info = null;
            if (gateway is IRangeMediaGateway range) info = await range.GetMediaInfoAsync(id, video, stop.Token);
            reader = info?.SupportsStreaming == true ? new ParallelMediaReader((IRangeMediaGateway)gateway, id, video, info, settings.Connections) : null;
            if (video && VideoPlayerPolicy.ShouldStream(settings.Mode, kind, reader is not null, executable.Length > 0))
            {
                proxy = await VideoPlaybackProxy.StartAsync(reader!, stop.Token);
                status.Text = "正在尝试流式播放；如果画面无法播放，可切换为下载。";
                fallback.IsEnabled = true;
                process = Launch(executable, proxy.Url, kind);
                var exit = process?.WaitForExitAsync(stop.Token) ?? Task.Delay(Timeout.Infinite, stop.Token);
                var first = await Task.WhenAny(proxy.FirstRead, exit, downloadRequested.Task, Task.Delay(TimeSpan.FromSeconds(20), stop.Token));
                stop.Token.ThrowIfCancellationRequested();
                if (first == proxy.FirstRead)
                {
                    status.Text = "播放器已开始读取视频流，可拖动跳转。关闭此窗口将断开播放。";
                    var done = await Task.WhenAny(exit, downloadRequested.Task, Task.Delay(Timeout.Infinite, stop.Token));
                    stop.Token.ThrowIfCancellationRequested();
                    if (done == exit) return;
                }
                await proxy.DisposeAsync(); proxy = null;
                status.Text = "已切换为下载；完成后使用所选播放器打开。";
                fallback.IsEnabled = false;
            }
            else status.Text = video ? "当前选择使用下载方式，完成后播放。" : "正在读取照片…";
            if (info is { Length: > 21474836480 }) throw new IOException("媒体超过 20 GB 本地缓存容量，请选择流式播放器。");
            var version = info?.SupportsStreaming == true ? info.ETag : Guid.NewGuid().ToString("N");
            var identity = hash is { Length: 64 } ? hash : $"{(gateway as HttpIndexGateway)?.MediaCacheScope ?? gateway.GetType().Name}:{id}:{video}:{version}";
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            var path = await Cache.Value.GetAsync(key, extension, hash, async (target, token) =>
            {
                if (reader is not null) await reader.DownloadAsync(target, token);
                else
                {
                    await using var input = video ? await gateway.OpenOriginalVideoAsync(id, cancellationToken: token) : await gateway.OpenOriginalImageAsync(id, cancellationToken: token);
                    if (input is null) throw new IOException("原文件不可用。");
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    { if (output.Length + read > 20L * 1024 * 1024 * 1024) throw new IOException("媒体超过缓存容量。"); await output.WriteAsync(buffer.AsMemory(0, read), token); }
                }
            }, stop.Token);
            Launch(executable, path, kind)?.Dispose();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception exception)
        { MessageBox.Show("读取或播放失败：" + (exception is InvalidDataException ? exception.Message : exception.GetType().Name), "播放失败"); }
        finally
        {
            try { if (proxy is not null) await proxy.DisposeAsync(); }
            finally
            {
                try { if (reader is not null) await reader.DisposeAsync(); }
                finally
                {
                    try { process?.Dispose(); window?.Close(); }
                    finally { lock (SessionGate) Sessions.Remove(finished.Task); finished.TrySetResult(); }
                }
            }
        }
    }
    private static Process? Launch(string executable, string target, VideoPlayerKind kind)
    {
        if (executable.Length == 0) return Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (kind == VideoPlayerKind.Vlc) { start.ArgumentList.Add("--no-one-instance"); start.ArgumentList.Add("--play-and-exit"); }
        if (kind == VideoPlayerKind.Mpv) start.ArgumentList.Add("--no-terminal");
        start.ArgumentList.Add(target);
        return Process.Start(start);
    }
}
