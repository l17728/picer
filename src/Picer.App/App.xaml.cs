using System.Diagnostics;
using System.IO;
using System.Windows;
using Picer.App.Infrastructure;
using Picer.App.ViewModels;
using Picer.Core.Diagnostics;

namespace Picer.App;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;
    private readonly CancellationTokenSource _coldStartSyncCts = new();
    private Task? _coldStartSync;
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
                var recovery = new Picer.Core.People.TrainingJobRecoveryService(AppServices.VisionRepository, AppServices.CreateTrainingSubmissionService);
                var recoveryTasks = recovery.ResumeAsync().GetAwaiter().GetResult();
                _ = Task.WhenAll(recoveryTasks);
                LogInfo($"已恢复后台训练任务监控：{recoveryTasks.Count} 个");
            }
        }
        catch (Exception ex)
        {
            LogCrash("AppServices.Initialize 失败", ex);
            var detail = ex.Message switch
            {
                "database_upgrade_requires_closed_instances" or "database_upgrade_requires_closed_database_wal_shm" or "database_startup_busy_close_other_instances"
                    => "索引需要升级。请关闭其他 Picer 窗口和使用该索引的服务，再重新启动；程序会先自动备份索引。",
                "database_schema_newer_than_application" => "此索引由更新版本的 Picer 创建，请使用相同或更新版本打开。",
                "database_migration_integrity_failed" or "database_migration_foreign_keys_failed"
                    => "索引升级校验未通过，原索引和迁移前备份已保留。请查看日志后再处理。",
                _ => ex.Message
            };
            MessageBox.Show($"初始化失败：{detail}\n\n详见 {LogPath}", "Picer",
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
                AppServices.Gateway as Picer.Core.Gateway.IMediaAssetGateway, AppServices.GetClientServices());
            _mainViewModel = new MainViewModel(AppServices.Db, AppServices.Index, AppServices.Geocoder, peopleViewModel, AppServices.GetClientServices());
            LogInfo("ViewModel 创建完成");

            LogInfo("正在创建 MainWindow...");
            var window = new MainWindow(_mainViewModel);
            window.Title = AppServices.ServiceMode ? "Picer - 图片管理（远程）" : "Picer - 图片管理（本地）";
            MainWindow = window;
            window.Show();
            if (AppServices.ServiceMode)
            {
                // Resolving the source machine's allow-listed roots can open a
                // large local index database. Never perform that startup I/O
                // on the WPF dispatcher; the shell should remain interactive
                // while the resident agent refreshes its configuration.
                _ = Task.Run(StartConfiguredMediaAgent);
            }
            // Service mode must always pull the authoritative remote mirror.
            // SyncClient is an optional publisher helper and can be absent when
            // a read-only endpoint was configured, but that must not disable
            // remote metadata refresh (video people/keyframes included).
            if (AppServices.ServiceMode || AppServices.SyncClient is not null || AppServices.LocalSpaceSyncConfigured)
                _coldStartSync = Task.Run(() => ColdStartSynchronizationAsync(_coldStartSyncCts.Token));
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
            if (AppPaths.LoadIndexBackend().DeviceAuthentication) return;
            LogInfo("正在后台同步 MediaAgent 授权源目录并刷新单实例代理");
            var settings = MediaAgentController.Load();
            if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.ServiceIp))
            {
                LogInfo($"远程模式未启用原图代理，跳过 MediaAgent 自动启动；enabled={settings.Enabled}; serviceIpConfigured={!string.IsNullOrWhiteSpace(settings.ServiceIp)}");
                return;
            }
            LogInfo("MediaAgent 启动：正在从镜像索引解析本机可代理源目录");
            var mirroredSources = UiWork.RunDatabaseAsync("MediaAgent.ResolveMirroredRoots", AppServices.Db.GetSources)
                .GetAwaiter().GetResult();
            var roots = mirroredSources.Select(source => source.Path).Where(Directory.Exists).ToArray();
            // A service-mode client normally owns only remote-mirror.db. If
            // this machine is also the source host, recover the real source
            // roots from its local index so the agent can serve media.
            if (roots.Length == 0 && File.Exists(AppPaths.DbPath))
            {
                LogInfo("MediaAgent 启动：镜像索引没有本机可用目录，检查本机权威索引");
                using var localDb = new Picer.Core.Data.PicerDatabase(AppPaths.DbPath);
                roots = localDb.GetSources().Select(source => source.Path).Where(Directory.Exists).ToArray();
            }
            roots = roots.Concat(MediaAgentController.LoadRepairRoots()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (roots.Length == 0)
            {
                LogInfo($"远程模式未找到可代理的本地源目录，跳过 MediaAgent 自动启动；mirroredSourceCount={mirroredSources.Count}; dataDbExists={File.Exists(AppPaths.DbPath)}");
                return;
            }
            LogInfo($"MediaAgent 启动：解析到本机授权根目录；count={roots.Length}");
            if (MediaAgentController.Start(settings.ServiceIp, MediaAgentController.ResolveToken(),
                    MediaAgentController.ResolveSpkiPin(), roots))
                LogInfo($"远程模式已自动启动 MediaAgent：service={settings.ServiceIp}; roots={roots.Length}");
            else
                LogInfo("远程模式无法启动 MediaAgent：程序不完整或启动后退出，请查看代理启动日志");
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
    private async Task ColdStartSynchronizationAsync(CancellationToken cancellationToken)
    {
        Picer.Core.Identity.SpaceNotification? notificationBaseline=null;
        LogInfo($"索引冷启动同步 pass 开始；mode={(AppServices.ServiceMode ? "remote" : "local")}");
        try
        {
            notificationBaseline=await AppServices.CaptureSpaceNotificationBaselineAsync(cancellationToken).ConfigureAwait(false);
            if (AppServices.ServiceMode)
                await SyncRemoteMirrorInBackgroundAsync(cancellationToken).ConfigureAwait(false);
            await SyncReplicationInBackgroundAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogInfo("索引冷启动同步已取消；checkpoint 已保留");
        }
        catch (Exception exception)
        {
            LogCrash("索引冷启动同步失败；checkpoint 已保留", exception);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
                AppServices.StartSpaceNotifications(async () =>
                {
                    if (_mainViewModel is not null)
                        await _mainViewModel.RefreshFromRemoteMirrorAsync(localSourceUpdate: !AppServices.ServiceMode);
                },notificationBaseline);
        }
    }

    private async Task SyncReplicationInBackgroundAsync(CancellationToken cancellationToken)
    {
        // Publish the local user's committed changes once. The publisher
        // checkpoint makes this idempotent; unchanged media/metadata produces
        // no network writes. Notification reconnect backoff never schedules a
        // periodic full-library scan.
        try
        {
            await AppServices.SyncLocalChangesToRemoteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogCrash("本地索引增量发布失败", exception);
        }
        // Only the service-mode mirror represents the aggregate photo space.
        // A local database must use its publisher-scoped download channel;
        // pulling the legacy whole-space mirror here could import another
        // device's private source records.
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
        _coldStartSyncCts.Cancel();
        try { _coldStartSync?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _coldStartSyncCts.Dispose();
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
