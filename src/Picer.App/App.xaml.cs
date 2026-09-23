using System.Diagnostics;
using System.IO;
using System.Windows;
using Picer.App.ViewModels;
using Picer.Core.Diagnostics;

namespace Picer.App;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;
    private readonly CancellationTokenSource _remoteMirrorLoopCts = new();
    private Task? _remoteMirrorLoop;
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
            LogInfo($"AppServices 初始化完成；serviceMode={AppServices.ServiceMode}; syncClient={(AppServices.SyncClient is null ? "未配置" : "已配置")}; gateway={AppServices.Gateway.GetType().Name}");
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
                AppServices.UnknownFaceClusters, AppServices.VisionUnavailableReason,
                AppServices.Gateway as Picer.Core.Gateway.IMediaAssetGateway);
            _mainViewModel = new MainViewModel(AppServices.Db, AppServices.Index, AppServices.Geocoder, peopleViewModel);
            LogInfo("ViewModel 创建完成");

            LogInfo("正在创建 MainWindow...");
            var window = new MainWindow(_mainViewModel);
            window.Title = AppServices.ServiceMode ? "Picer - 图片管理（远程）" : "Picer - 图片管理（本地）";
            MainWindow = window;
            window.Show();
            if (AppServices.ServiceMode)
            {
                StartConfiguredMediaAgent();
            }
            if (AppServices.SyncClient is not null)
                _remoteMirrorLoop = Task.Run(() => RemoteMirrorLoopAsync(_remoteMirrorLoopCts.Token));
            // A remote client is a read/mirror consumer. Publishing the local
            // evaluation snapshot here needlessly scans thousands of faces and
            // competes with mirror bootstrap, making the remote tree appear
            // empty. Only the authoritative local client publishes it.
            if (!AppServices.ServiceMode)
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

    private static void StartConfiguredMediaAgent()
    {
        try
        {
            var settings = MediaAgentController.Load();
            if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.ServiceIp))
            {
                LogInfo("远程模式未启用原图代理，跳过 MediaAgent 自动启动");
                return;
            }
            var roots = AppServices.Db.GetSources().Select(source => source.Path).Where(Directory.Exists).ToArray();
            // A service-mode client normally owns only remote-mirror.db. If
            // this machine is also the source host, recover the real source
            // roots from its local index so the agent can serve media.
            if (roots.Length == 0 && File.Exists(AppPaths.DbPath))
            {
                using var localDb = new Picer.Core.Data.PicerDatabase(AppPaths.DbPath);
                roots = localDb.GetSources().Select(source => source.Path).Where(Directory.Exists).ToArray();
            }
            if (roots.Length == 0)
            {
                LogInfo("远程模式未找到可代理的本地源目录，跳过 MediaAgent 自动启动");
                return;
            }
            if (MediaAgentController.Start(settings.ServiceIp, MediaAgentController.ResolveToken(),
                    MediaAgentController.ResolveSpkiPin(), roots))
                LogInfo($"远程模式已自动启动 MediaAgent：service={settings.ServiceIp}; roots={roots.Length}");
            else
                LogInfo("远程模式无法启动 MediaAgent：未找到可执行文件");
        }
        catch (Exception exception)
        {
            LogCrash("远程模式启动 MediaAgent 失败", exception);
        }
    }

    /// <summary>
    /// Keeps a service-mode client up to date after the initial bootstrap. The
    /// mirror is deliberately refreshed off the UI thread; a failed poll is
    /// isolated and retried on the next interval so a temporary network or
    /// SQLite lock never takes down the shell.
    /// </summary>
    private async Task RemoteMirrorLoopAsync(CancellationToken cancellationToken)
    {
        LogInfo($"索引双向同步后台轮询已启动，间隔 5 秒；mode={(AppServices.ServiceMode ? "remote" : "local")}");
        try
        {
            if (AppServices.ServiceMode)
                await SyncRemoteMirrorInBackgroundAsync(cancellationToken).ConfigureAwait(false);
            await SyncReplicationInBackgroundAsync(cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                await SyncReplicationInBackgroundAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogInfo("远程索引镜像后台轮询已停止");
        }
        catch (Exception exception)
        {
            LogCrash("远程索引镜像后台轮询失败", exception);
        }
    }

    private async Task SyncReplicationInBackgroundAsync(CancellationToken cancellationToken)
    {
        // Publish first so a remote client's mutation is not overwritten by an
        // older mirror pull. The checkpoint makes this incremental and
        // idempotent; unchanged media/metadata produces no network writes.
        try
        {
            await AppServices.SyncLocalChangesToRemoteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogCrash("本地索引增量发布失败", exception);
        }
        if (AppServices.SyncClient is not null)
            await SyncRemoteMirrorInBackgroundAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncRemoteMirrorInBackgroundAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
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
        _remoteMirrorLoopCts.Cancel();
        try { _remoteMirrorLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _remoteMirrorLoopCts.Dispose();
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
