using Picer.Core.Models;

namespace Picer.Core.Vision;

/// <summary>
/// Runs vision inference serially so background analysis cannot saturate memory or the GPU.
/// Source images are passed read-only to the analyzer and are never changed by this service.
/// </summary>
public sealed class VisionAnalysisService(
    VisionAnalysisRepository repository,
    IImageFaceAnalyzer analyzer) : IDisposable
{
    private readonly SemaphoreSlim _singleWorker = new(1, 1);
    private bool _disposed;

    public async Task<VisionBatchResult> AnalyzeAsync(
        IEnumerable<ImageRecord> images,
        IProgress<(int Completed, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var batch = images.Where(image => image.Status == ImageStatus.Active).ToList();
        var analyzed = 0;
        var skipped = 0;
        var failed = 0;
        var completed = 0;

        await _singleWorker.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var image in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (repository.IsCurrent(image, analyzer.AnalyzerName, analyzer.ModelVersion))
                {
                    skipped++;
                }
                else
                {
                    try
                    {
                        var result = await analyzer.AnalyzeAsync(image, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        repository.SaveCompleted(image, analyzer, result);
                        analyzed++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        // Persist only the exception type. Paths, face data, and exception messages may contain
                        // sensitive local information and must not enter the analysis database.
                        repository.SaveFailure(image, analyzer, exception.GetType().Name);
                        failed++;
                    }
                }

                completed++;
                progress?.Report((completed, batch.Count));
            }
        }
        finally
        {
            _singleWorker.Release();
        }

        return new VisionBatchResult(batch.Count, analyzed, skipped, failed);
    }

    /// <summary>Forces a fresh analysis of one explicitly selected photo, even when the current model result is cached.</summary>
    public async Task<FaceAnalysisResult> AnalyzeSingleAsync(
        ImageRecord image, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (image.Status != ImageStatus.Active)
            throw new InvalidOperationException("face_analysis_requires_active_image");

        await _singleWorker.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var result = await analyzer.AnalyzeAsync(image, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                repository.SaveCompleted(image, analyzer, result);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                repository.SaveFailure(image, analyzer, exception.GetType().Name);
                throw;
            }
        }
        finally
        {
            _singleWorker.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _singleWorker.Dispose();
    }
}
