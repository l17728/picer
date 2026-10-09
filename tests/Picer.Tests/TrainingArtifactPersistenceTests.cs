using System.Collections.Concurrent;
using Picer.ModelServer;

namespace Picer.Tests;

public sealed class TrainingArtifactPersistenceTests
{
    [Fact]
    public async Task FailedQualityGateStillKeepsSignedArtifactForReviewAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-training-audit-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("PICER_TRAINING_ARCHIVE_ROOT");
        var previousWorker = Environment.GetEnvironmentVariable("PICER_GPU_WORKER_PYTHON");
        try
        {
            Environment.SetEnvironmentVariable("PICER_TRAINING_ARCHIVE_ROOT", root);
            Environment.SetEnvironmentVariable("PICER_GPU_WORKER_PYTHON", null);
            using var signer = new ArtifactSigner();
            using var worker = new GpuTrainingWorkerHost();
            var archive = new TrainingAttemptArchive();
            var ranking = new ModelCandidateRankingStore();
            var executor = new TrainingExecutor(signer, archive, ranking, worker);
            var jobs = new ConcurrentDictionary<string, TrainingJob>();
            jobs["audit"] = new("audit", "queued", "queued", 0, null, null);
            await executor.RunAsync("audit", new DatasetRequest("audit", Samples: [
                new(1, 1, [1, 0]), new(2, 2, [0, 1]),
                new(3, 1, [-1, 0], "validation"), new(4, 2, [0, -1], "validation")]), jobs);
            Assert.Equal("completed", jobs["audit"].Status);
            var recreated = new TrainingExecutor(signer, archive, ranking, worker);
            Assert.True(recreated.TryGetArtifact(jobs["audit"].ArtifactId!, out var artifact));
            Assert.False(CalibrationGate.Passes(artifact.Metrics));
            Assert.NotEmpty(artifact.Signature!);
            Assert.NotEmpty(artifact.Sha256!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PICER_TRAINING_ARCHIVE_ROOT", previousRoot);
            Environment.SetEnvironmentVariable("PICER_GPU_WORKER_PYTHON", previousWorker);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
