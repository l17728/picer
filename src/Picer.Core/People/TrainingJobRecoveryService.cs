using Picer.Core.Vision;

namespace Picer.Core.People;

/// <summary>Resumes monitoring of remote jobs that survived an application restart.</summary>
public sealed class TrainingJobRecoveryService(VisionAnalysisRepository repository, Func<FaceTrainingSubmissionService>? clientFactory = null)
{
    public Task<IReadOnlyList<Task>> ResumeAsync(CancellationToken cancellationToken = default)
    {
        var tasks = repository.GetActiveTrainingJobs().Select(job => MonitorAsync(job, cancellationToken)).ToArray();
        return Task.FromResult<IReadOnlyList<Task>>(tasks);
    }

    private async Task MonitorAsync((string JobId, string Endpoint, string Status, string Stage, int Progress, string? ArtifactId) job, CancellationToken token)
    {
        try
        {
            using var client = clientFactory?.Invoke() ?? new FaceTrainingSubmissionService(job.Endpoint, Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY") ?? "");
            var monitor = new TrainingJobMonitor(client);
            var result = await monitor.WaitAsync(job.JobId, cancellationToken: token).ConfigureAwait(false);
            repository.UpsertTrainingJob(job.JobId, job.Endpoint, result.Status, result.Status, result.Status == "completed" ? 100 : job.Progress, result.ArtifactId, result.Error);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { repository.UpsertTrainingJob(job.JobId, job.Endpoint, "failed", "recovery", job.Progress, job.ArtifactId, exception.Message); }
    }
}
