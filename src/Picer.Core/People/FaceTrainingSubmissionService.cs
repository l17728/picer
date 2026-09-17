using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Picer.Core.People;

public sealed record TrainingSubmissionResult(string JobId, string Status, string? ArtifactId, string? Error,
    string? Stage = null, int Progress = 0);
public sealed record TrainingModelAvailability(string Id, string ModelVersion, string Name, bool Available,
    string EmbeddingVersion);
public sealed record ParallelTrainingSubmissionResult(string ModelVersion, TrainingSubmissionResult Result);
public sealed record BaselineTrainingSubmissionResult(string EvaluationId,
    IReadOnlyList<ParallelTrainingSubmissionResult> Jobs, string? Error = null);

public sealed record TrainingArtifactData(
    string ArtifactId, string Algorithm, string ModelVersion, DateTime CreatedUtc,
    IReadOnlyList<PrototypeData> Prototypes, MetricsData Metrics,
    string? Sha256 = null, string? Signature = null, string? SignatureAlgorithm = null, string? SigningKeyId = null,
    TrainingDiagnosticsData? Diagnostics = null);
public sealed record TrainingDiagnosticsData(string RecommendedAction, double ThresholdSweepBestAccuracy,
    double ThresholdSweepBestScale, double ThresholdSweepBestFar, double ThresholdSweepBestFrr,
    double CurrentAccuracy, string Explanation);

public sealed record ArtifactSigningInfoData(string Algorithm, string PublicKey, string KeyId);

public sealed record PrototypeData(long PersonId, float[] Centroid, double Threshold, int SampleCount);
public sealed record TrainingVisualAttribute(string Pose, string Lighting, IReadOnlyList<string> Occlusion,
    string Expression, double FaceQuality, bool IsUsefulForTraining, string ModelVersion, string PromptVersion,
    DateTime? CaptureUtc = null, string? BurstGroup = null, string? ContentHash = null);
public sealed record TrainingActionEvidence(long FaceId, long? PersonId, long? SourcePersonId,
    string Action, DateTime OccurredUtc, IReadOnlyList<long> CoClusteredFaceIds,
    IReadOnlyList<long> CoClusteredPersonIds, double? ClusterConfidence, string ModelVersion, string? ErrorType);

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
        _client = new HttpClient(handler) { BaseAddress = new Uri(NormalizeBaseUrl(baseUrl)), Timeout = TimeSpan.FromMinutes(10) };
        var resolvedKey = string.IsNullOrWhiteSpace(apiKey) ? ModelServerApiKey.Load() : apiKey.Trim();
        if (!string.IsNullOrWhiteSpace(resolvedKey))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", resolvedKey);
    }

    /// <summary>读取平台真实可用模型；不可用模型不会进入并行提交。</summary>
    public async Task<IReadOnlyList<TrainingModelAvailability>> GetAvailableAlgorithmsAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetAsync("api/face-training/algorithms", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];
            return await response.Content.ReadFromJsonAsync<TrainingModelAvailability[]>(Json, ct).ConfigureAwait(false) ?? [];
        }
        catch { return []; }
    }

    public async Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(string modelVersion,
        IReadOnlyList<string> faceImageBase64, CancellationToken ct = default)
    {
        if (faceImageBase64.Count == 0) return [];
        var response = await _client.PostAsJsonAsync("api/face-training/embeddings",
            new { modelVersion, images = faceImageBase64 }, Json, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [];
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct).ConfigureAwait(false);
        if (!payload.TryGetProperty("embeddings", out var embeddings) || embeddings.ValueKind != JsonValueKind.Array) return [];
        return embeddings.EnumerateArray().Select(item => item.EnumerateArray().Select(value => value.GetSingle()).ToArray()).ToArray();
    }

    /// <summary>
    /// 并行提交不同模型的数据集。每个模型独立捕获异常，单模型失败不取消其他任务。
    /// 调用方必须提供与模型版本匹配的向量或图像裁剪数据。
    /// </summary>
    public async Task<IReadOnlyList<ParallelTrainingSubmissionResult>> SubmitParallelAsync(
        FaceTrainingSnapshotResult snapshot,
        IReadOnlyDictionary<string, IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split)>> modelSamples,
        CancellationToken ct = default)
    {
        var tasks = modelSamples.Where(pair => pair.Value.Count > 0).Select(async pair =>
        {
            try
            {
                var result = await SubmitAsync(snapshot, pair.Value, ct, modelVersion: pair.Key).ConfigureAwait(false);
                return new ParallelTrainingSubmissionResult(pair.Key, result);
            }
            catch (Exception ex)
            {
                return new ParallelTrainingSubmissionResult(pair.Key,
                    new TrainingSubmissionResult(string.Empty, "failed", null, ex.Message));
            }
        });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Submit one immutable evaluation snapshot with model-specific
    /// vectors. The platform evaluates every supplied model under one round id;
    /// vectors are never reused across model versions.</summary>
    public async Task<BaselineTrainingSubmissionResult> SubmitBaselineAsync(
        FaceTrainingSnapshotResult snapshot,
        IReadOnlyDictionary<string, IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split)>> modelSamples,
        CancellationToken ct = default)
    {
        if (modelSamples.Count == 0) return new BaselineTrainingSubmissionResult(string.Empty, [], "没有可评估的模型数据");
        var evaluationId = $"baseline-{snapshot.SnapshotId}-{Guid.NewGuid():N}";
        var datasets = modelSamples.ToDictionary(pair => pair.Key, pair => pair.Value.Select(sample => new
        {
            faceId = sample.FaceId, personId = sample.PersonId, embedding = sample.Embedding,
            split = sample.Split,
            evidence = new { source = "evaluation-snapshot", modelVersion = pair.Key,
                groupId = $"snapshot:{snapshot.SnapshotId}:face:{sample.FaceId}" }
        }).ToArray());
        try
        {
            using var response = await _client.PostAsJsonAsync("api/face-training/model-rankings/baseline",
                new { snapshotId = snapshot.SnapshotId.ToString(), evaluationId, datasets }, Json, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new BaselineTrainingSubmissionResult(evaluationId, [], $"基线提交失败：{response.StatusCode}");
            var data = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct).ConfigureAwait(false);
            var jobs = data.TryGetProperty("jobs", out var jobArray) && jobArray.ValueKind == JsonValueKind.Array
                ? jobArray.EnumerateArray().Select(job => new ParallelTrainingSubmissionResult(
                    job.TryGetProperty("modelVersion", out var model) ? model.GetString() ?? string.Empty : string.Empty,
                    new TrainingSubmissionResult(job.GetProperty("id").GetString() ?? string.Empty,
                        job.TryGetProperty("status", out var status) ? status.GetString() ?? "queued" : "queued", null, null)))
                    .ToArray()
                : [];
            return new BaselineTrainingSubmissionResult(evaluationId, jobs);
        }
        catch (Exception exception)
        {
            return new BaselineTrainingSubmissionResult(evaluationId, [], exception.Message);
        }
    }

    public async Task<(bool Success, string? Error)> PublishEvaluationDatasetAsync(
        string snapshotId,
        IReadOnlyList<(long FaceId, long PersonId, string Split, string Group, string FaceImageBase64)> samples,
        CancellationToken ct = default)
    {
        if (samples.Count == 0) return (false, "评估快照没有可上传的人脸");
        try
        {
            var payload = new
            {
                snapshotId, scenario = "baseline", protocolVersion = "face-evidence.v1",
                samples = samples.Select(sample => new
                {
                    faceId = sample.FaceId, personId = sample.PersonId, split = sample.Split,
                    faceImageBase64 = sample.FaceImageBase64,
                    evidence = new { source = "immutable-evaluation-snapshot", groupId = sample.Group }
                }).ToArray()
            };
            using var response = await _client.PostAsJsonAsync("api/face-training/datasets", payload, Json, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (false, $"平台返回 {response.StatusCode}");
            return (true, null);
        }
        catch (Exception exception) { return (false, exception.Message); }
    }

    public async Task<BaselineTrainingSubmissionResult> SubmitBaselineAsync(
        Picer.Core.Vision.FaceEvaluationSnapshot snapshot,
        IReadOnlyDictionary<string, IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split, string Group)>> modelSamples,
        CancellationToken ct = default)
    {
        var converted = modelSamples.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split)> )pair.Value
                .Select(sample => (sample.FaceId, sample.PersonId, sample.Embedding, sample.Split)).ToArray());
        return await SubmitBaselineCoreAsync(snapshot.SnapshotId, converted, ct).ConfigureAwait(false);
    }

    private async Task<BaselineTrainingSubmissionResult> SubmitBaselineCoreAsync(string snapshotId,
        IReadOnlyDictionary<string, IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split)>> modelSamples,
        CancellationToken ct)
    {
        if (modelSamples.Count == 0) return new BaselineTrainingSubmissionResult(string.Empty, [], "没有可评估的模型数据");
        var evaluationId = $"baseline-{snapshotId}-{Guid.NewGuid():N}";
        var datasets = modelSamples.ToDictionary(pair => pair.Key, pair => pair.Value.Select(sample => new
        {
            faceId = sample.FaceId, personId = sample.PersonId, embedding = sample.Embedding,
            split = sample.Split,
            evidence = new { source = "evaluation-snapshot", modelVersion = pair.Key,
                groupId = $"snapshot:{snapshotId}:face:{sample.FaceId}" }
        }).ToArray());
        try
        {
            using var response = await _client.PostAsJsonAsync("api/face-training/model-rankings/baseline",
                new { snapshotId, evaluationId, datasets }, Json, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new BaselineTrainingSubmissionResult(evaluationId, [], $"基线提交失败：{response.StatusCode}");
            var data = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct).ConfigureAwait(false);
            var jobs = data.TryGetProperty("jobs", out var jobArray) && jobArray.ValueKind == JsonValueKind.Array
                ? jobArray.EnumerateArray().Select(job => new ParallelTrainingSubmissionResult(
                    job.TryGetProperty("modelVersion", out var model) ? model.GetString() ?? string.Empty : string.Empty,
                    new TrainingSubmissionResult(job.GetProperty("id").GetString() ?? string.Empty,
                        job.TryGetProperty("status", out var status) ? status.GetString() ?? "queued" : "queued", null, null)))
                    .ToArray()
                : [];
            return new BaselineTrainingSubmissionResult(evaluationId, jobs);
        }
        catch (Exception exception)
        {
            return new BaselineTrainingSubmissionResult(evaluationId, [], exception.Message);
        }
    }

    public async Task<TrainingSubmissionResult> SubmitAsync(FaceTrainingSnapshotResult snapshot,
        IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split)> samples,
        CancellationToken ct = default, int attempt = 1, string? trainingHints = null,
        IReadOnlyDictionary<long, TrainingVisualAttribute>? visualAttributes = null,
        IReadOnlyList<TrainingActionEvidence>? actions = null,
        string? recipeFingerprint = null, string? recipeReason = null, int excludedSampleCount = 0,
        string modelVersion = "sface")
    {
        try
        {
            var idempotencyKey = ComputeIdempotencyKey(snapshot.SnapshotId, modelVersion, recipeFingerprint, attempt);
            var payload = new
            {
                snapshotId = snapshot.SnapshotId.ToString(),
                modelVersion,
                attempt,
                trainingHints,
                recipeFingerprint,
                recipeReason,
                selectedSampleCount = samples.Count,
                excludedSampleCount,
                idempotencyKey,
                actions,
                protocolVersion = "face-evidence.v1",
                samples = samples.Select(s => new
                {
                    faceId = s.FaceId, personId = s.PersonId, embedding = s.Embedding, split = s.Split,
                    attributes = visualAttributes is not null && visualAttributes.TryGetValue(s.FaceId, out var visual) ? visual : null,
                    evidence = new
                    {
                        source = "user-confirmed",
                        modelVersion,
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

    private static string ComputeIdempotencyKey(long snapshotId, string modelVersion, string? recipeFingerprint, int attempt)
    {
        var value = $"{snapshotId}|{modelVersion}|{recipeFingerprint ?? "none"}|{attempt}|face-agent.v2";
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
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

    public async Task<string?> GetTrainingMemoryAsync(string? modelVersion = null, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetAsync("api/face-training/archive?limit=20", ct);
            if (!response.IsSuccessStatusCode) return null;
            var records = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (records.ValueKind != JsonValueKind.Array) return null;
            var lines = records.EnumerateArray()
                .Where(record => string.IsNullOrWhiteSpace(modelVersion)
                    || !record.TryGetProperty("modelVersion", out var mv)
                    || string.Equals(mv.GetString(), modelVersion, StringComparison.OrdinalIgnoreCase))
                .Select(record =>
            {
                var attempt = record.TryGetProperty("attempt", out var a) ? a.ToString() : "?";
                var status = record.TryGetProperty("status", out var s) ? s.GetString() : "unknown";
                var accuracy = record.TryGetProperty("validationAccuracy", out var v) ? v.ToString() : "-";
                var fingerprint = record.TryGetProperty("recipeFingerprint", out var f) ? f.GetString() : null;
                var reason = record.TryGetProperty("recipeReason", out var r) ? r.GetString() : null;
                var excluded = record.TryGetProperty("excludedSampleCount", out var e) ? e.ToString() : "0";
                return $"attempt={attempt};status={status};validation={accuracy};recipe={fingerprint ?? "unknown"};excluded={excluded};reason={reason ?? "unknown"}";
            });
            return string.Join("\n", lines);
        }
        catch { return null; }
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
