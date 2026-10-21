using Picer.App.Infrastructure;
using Picer.Core.Vision;

namespace Picer.App;

internal static class ClassificationJobCoordinator
{
    private static readonly object Sync = new();
    private static CancellationTokenSource? _cancellation;
    private static Task? _worker;
    private static string? _runId;
    public static event EventHandler<ClassificationRunProgress>? ProgressChanged;
    public static ClassificationRunProgress? Progress { get; private set; }

    public static void StartOrResume()
    {
        lock (Sync)
        {
            if (_worker is { IsCompleted: false }) return;
            _cancellation?.Dispose();
            _cancellation = new();
            var token = _cancellation.Token;
            _worker = RunAsync(token);
        }
    }

    public static async Task PauseAsync()
    {
        string? run;
        lock (Sync) { run = _runId; _cancellation?.Cancel(); }
        if (run is not null)
            await UiWork.RunDatabaseAsync("classification-pause", () => AppServices.ClassificationProjection.Pause(run));
        await RefreshAsync();
    }

    public static async Task CancelAsync()
    {
        string? run;
        lock (Sync) { run = _runId; _cancellation?.Cancel(); }
        if (run is not null)
            await UiWork.RunDatabaseAsync("classification-cancel", () => AppServices.ClassificationProjection.Cancel(run));
        await RefreshAsync();
    }

    public static Task RefreshAsync() => UiWork.RunDatabaseAsync("classification-status",
        () => AppServices.ClassificationProjection.GetLatestRun()).ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion && task.Result is { } progress) Publish(progress);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var progress = await UiWork.RunDatabaseAsync("classification-begin",
                AppServices.ClassificationProjection.BeginOrResume, cancellationToken);
            _runId = progress.RunId;
            Publish(progress);
            while (progress.Status == "running" && progress.Processed < progress.Total)
            {
                progress = await UiWork.RunDatabaseAsync("classification-batch",
                    () => AppServices.ClassificationProjection.ProcessNextBatch(progress.RunId, 40, cancellationToken),
                    cancellationToken);
                Publish(progress);
                await Task.Yield();
            }
            if (progress.Status == "running")
            {
                progress = await UiWork.RunDatabaseAsync("classification-finish",
                    () => AppServices.ClassificationProjection.ProcessNextBatch(progress.RunId, 40, cancellationToken),
                    cancellationToken);
                Publish(progress);
            }
        }
        catch (OperationCanceledException) { }
        finally { await RefreshAsync(); }
    }

    private static void Publish(ClassificationRunProgress progress)
    {
        Progress = progress;
        ProgressChanged?.Invoke(null, progress);
    }
}
