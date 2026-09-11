using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Picer.Core.People;

public sealed record TrainingSubmissionResult(string JobId, string Status, string? ArtifactId, string? Error);

public sealed record TrainingArtifactData(
    string ArtifactId, string Algorithm, string ModelVersion, DateTime CreatedUtc,
    IReadOnlyList<PrototypeData> Prototypes, MetricsData Metrics);

public sealed record PrototypeData(long PersonId, float[] Centroid, double Threshold, int SampleCount);

public sealed record MetricsData(
    int TotalSamples, int TotalPersons,
    double TrainAccuracy, double ValidationAccuracy,
    double FalseAcceptRate, double FalseRejectRate,
    double MeanSimilaritySame, double MeanSimilarityDiff);

public sealed class FaceTrainingSubmissionService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;

    public FaceTrainingSubmissionService(string baseUrl, string apiKey)
    {
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        _client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<TrainingSubmissionResult> SubmitAsync(FaceTrainingSnapshotResult snapshot,
        IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split)> samples,
        CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                snapshotId = snapshot.SnapshotId.ToString(),
                modelVersion = "sface",
                protocolVersion = "face-evidence.v1",
                samples = samples.Select(s => new
                {
                    faceId = s.FaceId, personId = s.PersonId, embedding = s.Embedding, split = s.Split,
                    evidence = new
                    {
                        source = "user-confirmed",
                        modelVersion = "sface",
                        selectionReason = s.Split == "validation" ? "独立验证集" : "用户已确认归类"
                    }
                })
            };
            var dataset = await _client.PostAsJsonAsync("api/face-training/datasets", payload, Json, ct);
            if (!dataset.IsSuccessStatusCode)
                return new TrainingSubmissionResult("", "failed", null, $"数据集提交失败：{dataset.StatusCode}");
            var datasetId = (await dataset.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("id").GetString();

            var jobPayload = new { datasetId, type = "prototype-calibration" };
            var job = await _client.PostAsJsonAsync("api/face-training/jobs", jobPayload, Json, ct);
            if (!job.IsSuccessStatusCode)
                return new TrainingSubmissionResult("", "failed", null, $"任务提交失败：{job.StatusCode}");
            var jobData = await job.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            var jobId = jobData.GetProperty("id").GetString() ?? "";
            return new TrainingSubmissionResult(jobId, jobData.GetProperty("status").GetString() ?? "queued", null, null);
        }
        catch (Exception exception)
        {
            return new TrainingSubmissionResult("", "failed", null, exception.Message);
        }
    }

    public async Task<TrainingSubmissionResult> PollAsync(string jobId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetAsync($"api/face-training/jobs/{jobId}", ct);
            if (!response.IsSuccessStatusCode)
                return new TrainingSubmissionResult(jobId, "failed", null, $"查询失败：{response.StatusCode}");
            var data = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            var status = data.GetProperty("status").GetString() ?? "unknown";
            var artifactId = data.TryGetProperty("artifactId", out var art) && art.ValueKind == JsonValueKind.String
                ? art.GetString() : null;
            var error = data.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
                ? err.GetString() : null;
            return new TrainingSubmissionResult(jobId, status, artifactId, error);
        }
        catch (Exception exception)
        {
            return new TrainingSubmissionResult(jobId, "failed", null, exception.Message);
        }
    }

    public async Task<TrainingArtifactData?> GetArtifactAsync(string artifactId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetAsync($"api/face-training/artifacts/{artifactId}", ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<TrainingArtifactData>(Json, ct);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}
