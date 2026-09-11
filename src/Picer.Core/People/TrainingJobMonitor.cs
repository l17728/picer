namespace Picer.Core.People;

/// <summary>Monitors a remote training job without blocking the UI thread.</summary>
public sealed class TrainingJobMonitor
{
    private readonly FaceTrainingSubmissionService _submission;

    public TrainingJobMonitor(FaceTrainingSubmissionService submission) => _submission = submission;

    public async Task<TrainingSubmissionResult> WaitAsync(
        string jobId,
        IProgress<TrainingJobProgress>? progress = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromHours(1));
        var token = timeoutSource.Token;
        var elapsed = TimeSpan.Zero;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            elapsed += TimeSpan.FromSeconds(1);
            var result = await _submission.PollAsync(jobId, token).ConfigureAwait(false);
            progress?.Report(new TrainingJobProgress(jobId, result.Status, elapsed));
            if (result.Status is "completed" or "failed" or "canceled") return result;
        }
    }
}

public sealed record TrainingJobProgress(string JobId, string Status, TimeSpan Elapsed);
