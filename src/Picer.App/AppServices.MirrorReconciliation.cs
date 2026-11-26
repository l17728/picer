using System.IO;
using System.Text;
using Picer.Core.Logging;

namespace Picer.App;

public static partial class AppServices
{
    private static readonly CancellationTokenSource MirrorReconciliationLifetime = new();
    private static int _mirrorReconciliationRequested;
    private static int _mirrorReconciliationWorkerRunning;
    private static Task? _mirrorReconciliationWorker;
    private static string MirrorReconciliationMarkerPath => Path.Combine(AppPaths.DataRoot, "remote-mirror-reconciliation.pending");

    /// <summary>
    /// Persist a dirty marker and keep reconciling the optional local mirror until a complete
    /// server pull/pending-write push succeeds. The server remains authoritative in service mode.
    /// Local-index/relay mode deliberately does not use this worker.
    /// </summary>
    public static void QueueRemoteMirrorReconciliation(string reason)
    {
        if (!ServiceMode) return;
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            var temporary = MirrorReconciliationMarkerPath + ".tmp";
            File.WriteAllText(temporary, "pending\n", Encoding.UTF8);
            File.Move(temporary, MirrorReconciliationMarkerPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn($"Remote mirror pending marker could not be persisted; errorType={exception.GetType().Name}");
        }

        Interlocked.Exchange(ref _mirrorReconciliationRequested, 1);
        if (Interlocked.CompareExchange(ref _mirrorReconciliationWorkerRunning, 1, 0) == 0)
            _mirrorReconciliationWorker = Task.Run(RunRemoteMirrorReconciliationAsync);
        var reasonCode = reason is "remote mirror sync failed" or "startup pending marker" or
            "cold start mirror timeout" or "cold start mirror failure" or "remote face analysis mirror lagging"
            ? reason : "unspecified";
        AppLogger.Info($"Remote mirror reconciliation queued; reason={reasonCode}");
    }

    /// <summary>Resume a previously persisted reconciliation after GUI startup.</summary>
    public static void ResumeRemoteMirrorReconciliation()
    {
        if (ServiceMode && File.Exists(MirrorReconciliationMarkerPath))
            QueueRemoteMirrorReconciliation("startup pending marker");
    }

    private static async Task RunRemoteMirrorReconciliationAsync()
    {
        var token = MirrorReconciliationLifetime.Token;
        try
        {
            do
            {
                Interlocked.Exchange(ref _mirrorReconciliationRequested, 0);
                await RetryRemoteMirrorUntilSuccessAsync(
                    syncToken => SyncRemoteMirrorAsync(syncToken, requireMetadataSync: true, scheduleRetryOnFailure: false),
                    token,
                    Task.Delay,
                    exception => AppLogger.Warn($"Remote mirror reconciliation retry deferred; errorType={exception.GetType().Name}"),
                    () =>
                    {
                        try { File.Delete(MirrorReconciliationMarkerPath); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            AppLogger.Warn($"Remote mirror reconciled but pending marker cleanup failed; errorType={exception.GetType().Name}");
                        }
                        AppLogger.Info("Remote mirror reconciliation completed after retry");
                    }).ConfigureAwait(false);
            } while (!token.IsCancellationRequested && Volatile.Read(ref _mirrorReconciliationRequested) != 0);
        }
        finally
        {
            Volatile.Write(ref _mirrorReconciliationWorkerRunning, 0);
            if (!token.IsCancellationRequested && Volatile.Read(ref _mirrorReconciliationRequested) != 0 &&
                Interlocked.CompareExchange(ref _mirrorReconciliationWorkerRunning, 1, 0) == 0)
                _mirrorReconciliationWorker = Task.Run(RunRemoteMirrorReconciliationAsync);
        }
    }

    internal static async Task RetryRemoteMirrorUntilSuccessAsync(Func<CancellationToken, Task> synchronize,
        CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action<Exception>? onFailure = null, Action? onSuccess = null)
    {
        delayAsync ??= Task.Delay;
        var delay = TimeSpan.FromSeconds(5);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await delayAsync(delay, cancellationToken).ConfigureAwait(false);
                await synchronize(cancellationToken).ConfigureAwait(false);
                onSuccess?.Invoke();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                onFailure?.Invoke(exception);
                delay = TimeSpan.FromSeconds(Math.Min(300, delay.TotalSeconds * 2));
            }
        }
    }

    private static void StopRemoteMirrorReconciliation()
    {
        MirrorReconciliationLifetime.Cancel();
        try { _mirrorReconciliationWorker?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
    }
}
