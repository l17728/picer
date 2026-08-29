using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed class SemanticAnalysisService(VisualConceptRepository repository, IImageSemanticAnalyzer analyzer)
{
    private readonly SemaphoreSlim _singleWorker = new(1, 1);

    public async Task<SemanticBatchResult> AnalyzeAsync(IEnumerable<ImageRecord> images,
        IProgress<(int Completed, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var batch = images.Where(image => image.Status == ImageStatus.Active).ToList();
        var analyzed = 0; var skipped = 0; var failed = 0; var completed = 0;
        await _singleWorker.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var image in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (repository.IsCurrent(image, analyzer)) skipped++;
                else try
                {
                    var result = await analyzer.AnalyzeAsync(image, cancellationToken).ConfigureAwait(false);
                    repository.Save(image, analyzer, result);
                    analyzed++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    repository.SaveFailure(image, analyzer, exception.GetType().Name);
                    failed++;
                }
                progress?.Report((++completed, batch.Count));
            }
        }
        finally { _singleWorker.Release(); }
        return new SemanticBatchResult(batch.Count, analyzed, skipped, failed);
    }
}
