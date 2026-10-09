using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Picer.Core.Geocoding;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.People;

namespace Picer.Core.Vision;

public sealed class CloudVisionAnalysisService(
    CloudVisionRepository repository,
    ICloudVisionAnalyzer analyzer,
    IReverseGeocoder geocoder,
    PeopleService? people = null,
    Func<Action, CancellationToken, Task>? databaseWork = null,
    bool allowConcurrent = false)
{
    private readonly SemaphoreSlim _singleWorker = new(1, 1);
    private async Task DatabaseAsync(Action work, CancellationToken token)
    {
        if (databaseWork is not null) { await databaseWork(work, token).ConfigureAwait(false); return; }
        await _singleWorker.WaitAsync(token).ConfigureAwait(false);
        try { work(); } finally { _singleWorker.Release(); }
    }
    private readonly SemaphoreSlim _serialRun = new(1, 1);

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
        if (!allowConcurrent) await _serialRun.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var item in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = (item.Image.ContentSha256 ?? item.Image.Sha256) is { Length: 64 } hash
                    ? hash.ToUpperInvariant() : "id:" + item.Image.Id;
                using (await AnalysisContentGate.EnterAsync(repository.DatabaseIdentity, identity, cancellationToken).ConfigureAwait(false))
                {
                    var location = await ResolveLocationAsync(item.Image, runPolicy.IncludePreciseGps, cancellationToken).ConfigureAwait(false);
                    var fingerprint = Fingerprint(location);
                    var current = false;
                    await DatabaseAsync(() => current = repository.IsCurrent(item.Image, analyzer, fingerprint), cancellationToken).ConfigureAwait(false);
                    if (current) skipped++;
                    else
                    {
                        var requestId = Guid.NewGuid().ToString("N");
                        var reserved = false;
                        await DatabaseAsync(() => reserved = repository.Billing.TryReserve(requestId,
                            runPolicy.EstimatedRequestCostYuan, runPolicy.DailyBudgetYuan, runPolicy.MonthlyBudgetYuan), cancellationToken).ConfigureAwait(false);
                        if (!reserved) { budgetBlocked = batch.Count - progressCount; break; }
                        var failureStage = AnalysisStage.Save;
                        try
                        {
                            IReadOnlyList<string> confirmedPeople = [];
                            await DatabaseAsync(() =>
                            {
                                repository.Queue(item.Image, analyzer, fingerprint, item.Reason);
                                repository.MarkProcessing(item.Image, analyzer, fingerprint, item.Reason);
                                confirmedPeople = people?.GetConfirmedPersonNamesForImage(item.Image.Id) ?? [];
                            }, cancellationToken).ConfigureAwait(false);
                            var effectiveTime = item.Image.TakenUtc ?? item.Image.CreatedUtc;
                            var trustedContext = JsonSerializer.Serialize(new
                            {
                                confirmedPeople,
                                takenAtLocal = effectiveTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                                timeSource = item.Image.TakenUtc is not null ? "photo-metadata" : item.Image.CreatedUtc is not null ? "file-metadata" : "unknown"
                            }, TrustedContextJson);
                            failureStage = AnalysisStage.Model;
                            var result = await analyzer.AnalyzeAsync(
                                new CloudVisionRequest(item.Image, location, item.LocalConcepts, trustedContext), cancellationToken).ConfigureAwait(false);
                            failureStage = AnalysisStage.Save;
                            await DatabaseAsync(() => repository.Save(item.Image, analyzer, fingerprint, item.Reason, result, requestId), CancellationToken.None).ConfigureAwait(false);
                            completed++;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            await DatabaseAsync(() => repository.MarkCanceled(item.Image, analyzer, fingerprint, item.Reason), CancellationToken.None).ConfigureAwait(false);
                            throw;
                        }
                        catch (Exception exception)
                        {
                            var failure = AnalysisStageException.Wrap(failureStage, exception);
                            var detail = $"{nameof(AnalysisStageException)}: {failure.Message}";
                            await DatabaseAsync(() => repository.SaveFailure(item.Image, analyzer, fingerprint, item.Reason,
                                detail[..Math.Min(detail.Length, 500)]), CancellationToken.None).ConfigureAwait(false);
                            failed++;
                        }
                        finally { await DatabaseAsync(() => repository.Billing.Release(requestId), CancellationToken.None).ConfigureAwait(false); }
                    }
                    progress?.Report((++progressCount, batch.Count));
                }
            }
        }
        finally { if (!allowConcurrent) _serialRun.Release(); }
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

    private static readonly JsonSerializerOptions TrustedContextJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
