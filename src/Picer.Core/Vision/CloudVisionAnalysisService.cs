using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Picer.Core.Geocoding;
using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.Core.Vision;

public sealed class CloudVisionAnalysisService(
    CloudVisionRepository repository,
    ICloudVisionAnalyzer analyzer,
    IReverseGeocoder geocoder)
{
    private readonly SemaphoreSlim _singleWorker = new(1, 1);

    public async Task<CloudVisionBatchResult> AnalyzeAsync(
        IEnumerable<(ImageRecord Image, IReadOnlyList<VisualConceptScore> LocalConcepts, string Reason)> candidates,
        IProgress<(int Completed, int Total)>? progress = null,
        CancellationToken cancellationToken = default,
        CloudVisionRunPolicy? runPolicy = null)
    {
        runPolicy ??= new CloudVisionRunPolicy(Enabled: true);
        var requested = candidates.Where(item => item.Image.Status == ImageStatus.Active).ToList();
        var batch = requested.Take(runPolicy.ClampCount(requested.Count)).ToList();
        var completed = 0; var skipped = 0; var failed = 0; var budgetBlocked = 0; var progressCount = 0;
        await _singleWorker.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var item in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = DateTime.Now;
                var dayStartUtc = now.Date.ToUniversalTime();
                var monthStartUtc = new DateTime(now.Year, now.Month, 1).ToUniversalTime();
                if (repository.GetEstimatedCostSince(dayStartUtc) >= runPolicy.DailyBudgetYuan ||
                    repository.GetEstimatedCostSince(monthStartUtc) >= runPolicy.MonthlyBudgetYuan)
                {
                    budgetBlocked = batch.Count - progressCount;
                    break;
                }
                var location = await ResolveLocationAsync(item.Image, runPolicy.IncludePreciseGps, cancellationToken).ConfigureAwait(false);
                var fingerprint = Fingerprint(location);
                if (repository.IsCurrent(item.Image, analyzer, fingerprint)) skipped++;
                else try
                {
                    repository.Queue(item.Image, analyzer, fingerprint, item.Reason);
                    repository.MarkProcessing(item.Image, analyzer, fingerprint, item.Reason);
                    var result = await analyzer.AnalyzeAsync(
                        new CloudVisionRequest(item.Image, location, item.LocalConcepts), cancellationToken).ConfigureAwait(false);
                    repository.Save(item.Image, analyzer, fingerprint, item.Reason, result);
                    completed++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    repository.Queue(item.Image, analyzer, fingerprint, item.Reason);
                    throw;
                }
                catch (Exception exception)
                {
                    var detail = $"{exception.GetType().Name}: {exception.Message}";
                    repository.SaveFailure(item.Image, analyzer, fingerprint, item.Reason,
                        detail[..Math.Min(detail.Length, 500)]);
                    failed++;
                }
                progress?.Report((++progressCount, batch.Count));
            }
        }
        finally { _singleWorker.Release(); }
        return new CloudVisionBatchResult(batch.Count, completed, skipped, failed, budgetBlocked);
    }

    private async Task<PhotoLocationContext?> ResolveLocationAsync(ImageRecord image, bool includePreciseGps, CancellationToken cancellationToken)
    {
        if (!GeoPlaceResolver.TryParseCoordinates(image.Gps, out var latitude, out var longitude)) return null;
        var place = await geocoder.ReverseAsync(latitude, longitude, cancellationToken).ConfigureAwait(false);
        return new PhotoLocationContext(includePreciseGps ? latitude : null, includePreciseGps ? longitude : null, place?.DisplayName,
            place?.Country, place?.Province, place?.City, place?.District, place?.Poi,
            place?.NearbyLandmarks, place?.Accuracy ?? "estimated");
    }

    private static string Fingerprint(PhotoLocationContext? location)
    {
        var json = JsonSerializer.Serialize(location);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
