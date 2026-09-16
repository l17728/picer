using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed record FaceVectorBackfillProgress(string ModelVersion, int Completed, int Total,
    int Analyzed, int Skipped, int Failed, int Attempt);

public sealed record FaceVectorBackfillResult(string ModelVersion, int Total, int Analyzed,
    int Skipped, int Failed, int Attempts, bool Completed);

/// <summary>
/// Incrementally materializes one model's face vectors without touching vectors
/// owned by other model versions. Existing successful records are skipped by
/// VisionAnalysisService; failed records are retried in bounded passes.
/// </summary>
public sealed class FaceVectorBackfillService(PicerDatabase database, IImageFaceAnalyzer analyzer)
{
    public async Task<FaceVectorBackfillResult> RunAsync(
        IProgress<FaceVectorBackfillProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int maxAttempts = 3)
    {
        if (maxAttempts is < 1 or > 9) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (string.IsNullOrWhiteSpace(analyzer.ModelVersion)) throw new ArgumentException("Model version is required", nameof(analyzer));

        var images = database.GetImages(includeMissing: false)
            .Where(image => image.Status == ImageStatus.Active && File.Exists(image.Path))
            .ToArray();
        var analyzed = 0;
        var skipped = 0;
        var failed = 0;
        var attempts = 0;
        for (attempts = 1; attempts <= maxAttempts; attempts++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var service = new VisionAnalysisService(new VisionAnalysisRepository(database), analyzer);
            VisionBatchResult batch;
            try
            {
                batch = await service.AnalyzeAsync(images,
                    new Progress<(int Completed, int Total)>(value =>
                        progress?.Report(new FaceVectorBackfillProgress(analyzer.ModelVersion, value.Completed,
                            value.Total, analyzed, skipped, failed, attempts))), cancellationToken).ConfigureAwait(false);
            }
            finally { service.Dispose(); }

            analyzed += batch.Analyzed;
            skipped += batch.Skipped;
            failed = batch.Failed;
            progress?.Report(new FaceVectorBackfillProgress(analyzer.ModelVersion, images.Length, images.Length,
                analyzed, skipped, failed, attempts));
            if (batch.Failed == 0) break;
        }

        return new(analyzer.ModelVersion, images.Length, analyzed, skipped, failed, attempts, failed == 0);
    }
}
