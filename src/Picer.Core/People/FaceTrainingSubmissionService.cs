using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Picer.Core.People;

public sealed record TrainingSubmissionResult(string JobId, string Status, string? ArtifactId, string? Error,
    string? Stage = null, int Progress = 0);

public sealed record TrainingArtifactData(
    string ArtifactId, string Algorithm, string ModelVersion, DateTime CreatedUtc,
    IReadOnlyList<PrototypeData> Prototypes, MetricsData Metrics,
    string? Sha256 = null, string? Signature = null, string? SignatureAlgorithm = null, string? SigningKeyId = null);

public sealed record ArtifactSigningInfoData(string Algorithm, string PublicKey, string KeyId);

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
        _client = new HttpClient(handler) { BaseAddress = new Uri(NormalizeBaseUrl(baseUrl)), Timeout = TimeSpan.FromSeconds(30) };
        var resolvedKey = string.IsNullOrWhiteSpace(apiKey) ? ModelServerApiKey.Load() : apiKey.Trim();
        if (!string.IsNullOrWhiteSpace(resolvedKey))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", resolvedKey);
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
            var status = jobData.GetProperty("status").GetString() ?? "queued";
            var stage = jobData.TryGetProperty("stage", out var initialStage) ? initialStage.GetString() : null;
            var progress = jobData.TryGetProperty("progress", out var initialProgress) ? initialProgress.GetInt32() : 0;
            return new TrainingSubmissionResult(jobId, status, null, null, stage, progress);
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
            var stage = data.TryGetProperty("stage", out var stageValue) && stageValue.ValueKind == JsonValueKind.String
                ? stageValue.GetString() : null;
            var progress = data.TryGetProperty("progress", out var progressValue) && progressValue.ValueKind == JsonValueKind.Number
                ? progressValue.GetInt32() : 0;
            return new TrainingSubmissionResult(jobId, status, artifactId, error, stage, progress);
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

    public async Task<bool> VerifyArtifactAsync(TrainingArtifactData artifact, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artifact.Sha256) || string.IsNullOrWhiteSpace(artifact.Signature)
            || !string.Equals(artifact.SignatureAlgorithm, "ecdsa-p256-sha256", StringComparison.OrdinalIgnoreCase))
            return false;
        var signing = await _client.GetFromJsonAsync<ArtifactSigningInfoData>("api/face-training/signing-key", Json, ct);
        if (signing is null || !string.Equals(signing.KeyId, artifact.SigningKeyId, StringComparison.Ordinal)) return false;
        var unsigned = artifact with { Sha256 = null, Signature = null, SignatureAlgorithm = null, SigningKeyId = null };
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(unsigned, Json));
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(artifact.Sha256))) return false;
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(signing.PublicKey), out _);
        return verifier.VerifyHash(hash, Convert.FromBase64String(artifact.Signature));
    }

    public void Dispose() => _client.Dispose();

    private static string NormalizeBaseUrl(string value)
    {
        var uri = new Uri(value.TrimEnd('/') + "/", UriKind.Absolute);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (uri.IsLoopback && uri.Port == 8090 &&
            !path.Equals("/picer", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/picer/", StringComparison.OrdinalIgnoreCase))
            return uri.GetLeftPart(UriPartial.Authority) + "/picer/";
        return uri.ToString();
    }
}
