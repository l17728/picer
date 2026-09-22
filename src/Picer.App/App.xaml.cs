using System.Diagnostics;
using System.IO;
using System.Windows;
using Picer.App.ViewModels;
using Picer.Core.Diagnostics;

namespace Picer.App;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;
    private static readonly string LogPath = Path.Combine(AppPaths.DataRoot, "logs", "crash.log");
    private static readonly object CrashLogLock = new();
    private static string? _lastCrashFingerprint;
    private static DateTime _lastCrashUtc;

    protected override void OnStartup(StartupEventArgs e)
    {
        var startupTimestamp = Stopwatch.GetTimestamp();
        base.OnStartup(e);

        var waitIndex = Array.IndexOf(e.Args, "--wait-for-pid");
        if (waitIndex >= 0 && waitIndex + 1 < e.Args.Length && int.TryParse(e.Args[waitIndex + 1], out var previousProcessId))
        {
            try { using var previous = Process.GetProcessById(previousProcessId); previous.WaitForExit(30000); } catch (ArgumentException) { }
        }

        try { DataProtectionService.ApplyPendingMigrationIfAny(); DataProtectionService.ApplyPendingRestoreIfAny(); }
        catch (Exception ex)
        {
            MessageBox.Show($"应用待处理恢复失败：{ex.Message}\n原数据未被删除，可从恢复快照处理。", "Picer 恢复失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            LogCrash("AppDomain.UnhandledException", ex);
        };

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        LogInfo("=== Picer 启动 ===");
        LogInfo($"工作目录: {Directory.GetCurrentDirectory()}");
        LogInfo($".NET 版本: {Environment.Version}");

        try
        {
            LogInfo("正在初始化 AppServices...");
            AppServices.Initialize();
            LogInfo("AppServices 初始化完成");
            if (!AppServices.ServiceMode)
            {
                var recovery = new Picer.Core.People.TrainingJobRecoveryService(AppServices.VisionRepository);
                var recoveryTasks = recovery.ResumeAsync().GetAwaiter().GetResult();
                _ = Task.WhenAll(recoveryTasks);
                LogInfo($"已恢复后台训练任务监控：{recoveryTasks.Count} 个");
            }
        }
        catch (Exception ex)
        {
            LogCrash("AppServices.Initialize 失败", ex);
            MessageBox.Show($"初始化失败：{ex.Message}\n\n详见 {LogPath}", "Picer",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        try
        {
            LogInfo("正在创建 ViewModel...");
            if (AppServices.ServiceMode)
                LogInfo("service 模式：将在后台同步远程索引镜像");
            var peopleViewModel = new PeopleViewModel(
                AppServices.Db, AppServices.People, AppServices.VisionRepository,
                AppServices.VisionAnalysis, AppServices.FaceThumbnails, AppServices.FaceSuggestions,
                AppServices.UnknownFaceClusters, AppServices.VisionUnavailableReason);
            _mainViewModel = new MainViewModel(AppServices.Db, AppServices.Index, AppServices.Geocoder, peopleViewModel);
            LogInfo("ViewModel 创建完成");

            LogInfo("正在创建 MainWindow...");
            var window = new MainWindow(_mainViewModel);
            window.Title = AppServices.ServiceMode ? "Picer - 图片管理（远程）" : "Picer - 图片管理（本地）";
            MainWindow = window;
            window.Show();
            if (AppServices.ServiceMode)
                _ = SyncRemoteMirrorInBackgroundAsync();
            _ = peopleViewModel.PublishEvaluationSnapshotForPlatformAsync();
            PicerEventSource.Log.StartupStage("shell-visible", Stopwatch.GetElapsedTime(startupTimestamp).TotalMilliseconds);
            LogInfo("MainWindow 已显示");
        }
        catch (Exception ex)
        {
            LogCrash("启动窗口失败", ex);
            MessageBox.Show($"启动失败：{ex.Message}\n\n详见 {LogPath}", "Picer",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private async Task SyncRemoteMirrorInBackgroundAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await AppServices.SyncRemoteMirrorAsync(timeout.Token);
            if (_mainViewModel is not null)
            {
                await _mainViewModel.RefreshFromRemoteMirrorAsync();
                // SQLite readers can briefly retain the pre-commit snapshot when
                // the metadata batch finishes on a background connection. Re-read
                // once after the commit boundary so the tree never remains partial.
                await Task.Delay(250, timeout.Token);
                await _mainViewModel.RefreshFromRemoteMirrorAsync();
            }
            LogInfo("远程索引镜像同步完成");
        }
        catch (OperationCanceledException)
        {
            LogInfo("远程索引镜像同步超时，保留已同步内容");
        }
        catch (Exception exception)
        {
            LogCrash("远程索引镜像同步失败", exception);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogInfo("=== Picer 退出 ===");
        _mainViewModel?.Dispose();
        AppServices.Shutdown();
        base.OnExit(e);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var fingerprint = $"{source}|{ex?.GetType().FullName}|{ex?.StackTrace?.Split('\n').FirstOrDefault()}";
            lock (CrashLogLock)
            {
                var now = DateTime.UtcNow;
                if (fingerprint == _lastCrashFingerprint && now - _lastCrashUtc < TimeSpan.FromSeconds(10)) return;
                _lastCrashFingerprint = fingerprint;
                _lastCrashUtc = now;
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                TrimOversizedCrashLog();
                var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CRASH: {source}\n{ex}\n\n";
                File.AppendAllText(LogPath, msg);
                Debug.Write(msg);
            }
        }
        catch { }
    }

    private static void LogInfo(string message)
    {
        var dir = Path.GetDirectoryName(LogPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        lock (CrashLogLock)
        {
            TrimOversizedCrashLog();
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] INFO: {message}\n";
            File.AppendAllText(LogPath, msg);
            Debug.Write(msg);
        }
    }

    private static void TrimOversizedCrashLog()
    {
        if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 20 * 1024 * 1024)
            File.Delete(LogPath);
    }
}
