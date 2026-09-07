using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Picer.Core.Data;

namespace Picer.Core.Vision;

public sealed record FaceTrainingProgress(string Stage, int Percent, string Detail);
public sealed record FaceTrainingResult(string DatasetId, string JobId, string ArtifactId, int Samples,
    int People, double FalseAcceptRate, double FalseRejectRate, bool Applied, string Message,
    int SelectedTrainingSamples = 0, int ExcludedSameSource = 0, int ExcludedPersonCap = 0,
    double? BaselineFalseAcceptRate = null, double? BaselineFalseRejectRate = null);
public sealed record FaceTrainingPlan(string SnapshotId, string ModelVersion, int Samples, int People,
    int PositiveSamples, int NegativeSamples, int LeakageGroups, int TrainSamples, int ValidationSamples,
    int TestSamples, int VideoSamples, IReadOnlyList<string> Warnings);

public sealed class FaceTrainingService(
    VisionAnalysisRepository repository,
    HttpClient? client = null) : IDisposable
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    private readonly bool _ownsClient = client is null;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public FaceTrainingPlan Prepare()
    {
        var samples = BuildSamples(out var modelVersion);
        var people = samples.Where(sample => sample.Label == "positive").Select(sample => sample.PersonId).Distinct().ToArray();
        if (people.Length < 2) throw new InvalidOperationException("至少需要两位已有人工确认样本的人物。");
        var snapshotId = new FaceEvaluationSnapshotService(repository).CreateSnapshot(
            adapterVersion: repository.GetActiveFaceAdapterVersion(modelVersion)).SnapshotId;
        var warnings = new List<string>();
        var readiness = new FaceTrainingReadinessService(repository).Check(snapshotId);
        warnings.AddRange(readiness.Warnings);
        if (samples.Where(sample => sample.Label == "positive").GroupBy(sample => sample.PersonId).Any(group => group.Count() < 10)) warnings.Add("部分人物少于 10 个正样本");
        if (samples.Count(sample => sample.Label == "negative") < 10) warnings.Add("排他负样本少于 10 个");
        if (!samples.Any(sample => sample.Partition == "test")) warnings.Add("独立测试分区为空，制品只能生成报告而不能自动应用");
        var uncovered = samples.Where(sample => sample.Label == "positive").GroupBy(sample => sample.PersonId)
            .Count(group => !group.Any(sample => sample.Partition == "train") ||
                            !group.Any(sample => sample.Partition is "validation" or "test"));
        if (uncovered > 0) warnings.Add($"{uncovered} 位人物缺少独立训练或评估来源组，需补充不同时间/视频的确认样本");
        return new(snapshotId, modelVersion, samples.Length, people.Length,
            samples.Count(sample => sample.Label == "positive"), samples.Count(sample => sample.Label == "negative"),
            samples.Select(sample => sample.LeakageGroup).Distinct().Count(), samples.Count(sample => sample.Partition == "train"),
            samples.Count(sample => sample.Partition == "validation"), samples.Count(sample => sample.Partition == "test"),
            samples.Count(sample => sample.FaceId < 0), warnings);
    }

    public async Task<FaceTrainingResult> SubmitAndApplyAsync(Uri endpoint,
        IProgress<FaceTrainingProgress>? progress = null, CancellationToken cancellationToken = default,
        string? expectedSnapshotId = null)
    {
        progress?.Report(new("snapshot", 2, "正在冻结人工确认与排他负样本"));
        var samples = BuildSamples(out var modelVersion);
        var people = samples.Where(sample => sample.Label == "positive").Select(sample => sample.PersonId).Distinct().ToArray();
        if (people.Length < 2) throw new InvalidOperationException("至少需要两位已有人工确认样本的人物。");
        var snapshotId = new FaceEvaluationSnapshotService(repository).CreateSnapshot(
            adapterVersion: repository.GetActiveFaceAdapterVersion(modelVersion)).SnapshotId;
        if (expectedSnapshotId is not null && !snapshotId.Equals(expectedSnapshotId, StringComparison.Ordinal))
            throw new InvalidOperationException("确认后人工标注发生了变化；为避免提交错位数据，请重新确认训练快照。");
        var root = new Uri(endpoint.ToString().TrimEnd('/') + "/");
        progress?.Report(new("upload", 10, $"正在提交 {samples.Length} 个脱敏向量样本"));
        using var datasetRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "datasets")) { Content = JsonContent.Create(new { snapshotId, sourceApp = "picer", modelVersion, samples }, options: Json) };
        datasetRequest.Headers.Add("X-Picer-Api-Version", "1");
        using var datasetResponse = await _client.SendAsync(datasetRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(datasetResponse, "提交训练快照", cancellationToken).ConfigureAwait(false);
        var dataset = await datasetResponse.Content.ReadFromJsonAsync<DatasetResponse>(Json, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("训练平台未返回数据集编号。");

        progress?.Report(new("queued", 18, "训练快照已接收，正在创建任务"));
        if (string.IsNullOrWhiteSpace(dataset.Id)) throw new InvalidDataException("训练平台返回了空数据集编号。");
        using var jobRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "jobs")) { Content = JsonContent.Create(new { datasetId = dataset.Id, type = "embedding-adapter-training" }, options: Json) };
        jobRequest.Headers.Add("X-Picer-Api-Version", "1");
        using var jobResponse = await _client.SendAsync(jobRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(jobResponse, "创建训练任务", cancellationToken).ConfigureAwait(false);
        var job = await jobResponse.Content.ReadFromJsonAsync<JobResponse>(Json, cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidDataException("训练平台未返回任务编号。");
        if (string.IsNullOrWhiteSpace(job.Id)) throw new InvalidDataException("训练平台返回了空任务编号。");
        var submittedJobId = job.Id;
        using var cancelRegistration = cancellationToken.Register(() =>
            _ = _client.PostAsync(new Uri(root, $"jobs/{submittedJobId}/cancel"), content: null, CancellationToken.None));
        while (job.Status is "queued" or "running" or "canceling")
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            job = await _client.GetFromJsonAsync<JobResponse>(new Uri(root, $"jobs/{job.Id}"), Json, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("训练平台返回了空任务状态。");
            if (!string.IsNullOrWhiteSpace(job.LogsUrl))
            {
                try
                {
                    var log = await _client.GetStringAsync(new Uri(root, job.LogsUrl.TrimStart('/')), cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(log)) progress?.Report(new(job.Stage, Math.Clamp(18 + job.Progress * 7 / 10, 18, 88), $"平台日志：{log.Trim().Split('\n').LastOrDefault()?.Trim()}"));
                }
                catch (HttpRequestException) { /* status polling remains authoritative */ }
            }
            progress?.Report(new(job.Stage, Math.Clamp(18 + job.Progress * 7 / 10, 18, 88),
                $"统一平台：{job.Stage} {job.Progress}%"));
        }
        if (job.Status != "ready" || string.IsNullOrWhiteSpace(job.ArtifactId))
            throw new InvalidOperationException($"训练任务{job.Status}：{job.Error ?? "未生成制品"}");
        progress?.Report(new("download", 90, "正在校验训练制品与评估指标"));
        var artifactJson = await _client.GetStringAsync(new Uri(root, $"artifacts/{job.ArtifactId}"), cancellationToken)
            .ConfigureAwait(false);
        var artifact = JsonSerializer.Deserialize<ArtifactResponse>(artifactJson, Json)
                       ?? throw new InvalidDataException("训练制品格式无效。");
        if (artifact.BaseModelVersion != modelVersion) throw new InvalidDataException("训练制品基础模型版本不匹配。");
        if (!string.IsNullOrWhiteSpace(artifact.Sha256))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(artifactJson))).ToLowerInvariant();
            if (!string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("训练制品 SHA-256 校验失败。");
        }
        if (!string.IsNullOrWhiteSpace(artifact.Signature) && !string.Equals(artifact.SignatureAlgorithm, "sha256", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"训练制品签名算法不受支持：{artifact.SignatureAlgorithm}");
        if (artifact.People.Any(value => !people.Contains(value.PersonId))) throw new InvalidDataException("训练制品包含快照以外的人物编号。");
        var applied = artifact.Metrics.FalseAcceptRate <= 0.01 && artifact.Metrics.PositiveEvaluated >= 20 &&
                      artifact.Metrics.NegativeEvaluated >= 10 && artifact.People.Count >= 2 &&
                      artifact.Comparison is { NoFalseAcceptRegression: true, AcceptableFalseRejectRegression: true };
        if (applied)
        {
            repository.ApplyLearnedFaceArtifact(artifact.Id, dataset.Id, root.ToString(), modelVersion, artifactJson,
                artifact.Metrics.FalseAcceptRate, artifact.Metrics.FalseRejectRate,
                artifact.People.Select(value => new FaceLearnedPolicy(value.PersonId, artifact.Id, modelVersion,
                    value.Prototype, value.Threshold)).ToArray());
        }
        progress?.Report(new(applied ? "applied" : "review", 100,
            applied ? "评估门禁通过，增强策略已应用" : "误识率未通过门禁，制品未应用"));
        return new(dataset.Id, job.Id, artifact.Id, samples.Length, artifact.People.Count,
            artifact.Metrics.FalseAcceptRate, artifact.Metrics.FalseRejectRate, applied,
            applied ? "训练完成并已应用；人工分类未改变。" : ExplainGateFailure(artifact),
            artifact.RecipeReport?.SelectedTrainingPositives ?? 0,
            artifact.RecipeReport?.ExcludedSameSource ?? 0, artifact.RecipeReport?.ExcludedPersonCap ?? 0,
            artifact.BaselineMetrics?.FalseAcceptRate, artifact.BaselineMetrics?.FalseRejectRate);
    }

    public string Rollback()
    {
        var previous = repository.GetPreviousLearnedFaceArtifact()
            ?? throw new InvalidOperationException("没有可回滚的上一版人脸增强策略。");
        var artifact = JsonSerializer.Deserialize<ArtifactResponse>(previous.Json, Json)
            ?? throw new InvalidDataException("上一版训练制品已损坏。");
        repository.ApplyLearnedFaceArtifact(previous.ArtifactId, previous.DatasetId, previous.Endpoint,
            previous.ModelVersion, previous.Json, previous.FalseAcceptRate, previous.FalseRejectRate,
            artifact.People.Select(value => new FaceLearnedPolicy(value.PersonId, previous.ArtifactId,
                previous.ModelVersion, value.Prototype, value.Threshold)).ToArray());
        return previous.ArtifactId;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, string operation, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        throw new InvalidOperationException($"{operation}失败（{(int)response.StatusCode}）：{detail}");
    }

    private Sample[] BuildSamples(out string modelVersion)
    {
        var confirmed = repository.GetEmbeddingsForMatching()
            .Where(face => face.AssignmentState == "confirmed" && face.PersonId is not null).ToArray();
        var selectedModelVersion = confirmed.GroupBy(face => face.ModelVersion).OrderByDescending(group => group.Count())
            .Select(group => group.Key).FirstOrDefault() ?? throw new InvalidOperationException("没有可训练的已确认人脸向量。");
        confirmed = confirmed.Where(face => face.ModelVersion == selectedModelVersion).ToArray();
        var rejected = repository.GetRejectedFaceEmbeddings().Where(face => face.ModelVersion == selectedModelVersion).ToArray();
        modelVersion = selectedModelVersion;
        var imageGroups = repository.GetTrainingLeakageGroups(confirmed.Select(face => face.ImageId)
            .Concat(rejected.Select(face => face.ImageId)));
        var photos = confirmed.Select(face => CreateSample(face.FaceId, face.PersonId!.Value, "positive",
                imageGroups.GetValueOrDefault(face.ImageId, $"image:{face.ImageId}"), face.Embedding,
                face.DetectionConfidence, face.RelativeArea))
            .Concat(rejected.Select(face => CreateSample(face.FaceId, face.PersonId, "negative",
                imageGroups.GetValueOrDefault(face.ImageId, $"image:{face.ImageId}"), face.Embedding,
                face.DetectionConfidence, face.RelativeArea)))
            .Concat(BuildConfirmedHardNegatives(confirmed, imageGroups));
        var videos = repository.GetVideoTrainingFaces().Select(face => CreateSample(-face.VideoFaceId, face.PersonId,
            face.Label, $"video:{face.VideoId}", face.Embedding, face.DetectionConfidence, face.RelativeArea));
        return photos.Concat(videos).OrderBy(sample => sample.FaceId).ThenBy(sample => sample.PersonId)
            .ThenBy(sample => sample.Label, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<Sample> BuildConfirmedHardNegatives(IReadOnlyList<FaceEmbeddingRecord> confirmed,
        IReadOnlyDictionary<long, string> imageGroups)
    {
        foreach (var person in confirmed.GroupBy(face => face.PersonId!.Value))
        {
            var positives = person.ToArray();
            var prototype = Mean(positives.Select(face => face.Embedding));
            var limit = Math.Clamp(positives.Length / 2, 10, 80);
            var usedGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in confirmed.Where(face => face.PersonId != person.Key)
                         .Select(face => (Face: face, Score: Cosine(prototype, face.Embedding)))
                         .OrderByDescending(value => value.Score))
            {
                var leakageGroup = imageGroups.GetValueOrDefault(candidate.Face.ImageId, $"image:{candidate.Face.ImageId}");
                if (!usedGroups.Add(leakageGroup)) continue;
                yield return CreateSample(candidate.Face.FaceId, person.Key, "negative", leakageGroup,
                    candidate.Face.Embedding, candidate.Face.DetectionConfidence, candidate.Face.RelativeArea);
                if (usedGroups.Count >= limit) break;
            }
        }
    }

    private static float[] Mean(IEnumerable<float[]> source)
    {
        var vectors = source.ToArray();
        var mean = new float[vectors[0].Length];
        foreach (var vector in vectors)
            for (var index = 0; index < mean.Length; index++) mean[index] += vector[index];
        var norm = Math.Sqrt(mean.Sum(value => value * value));
        if (norm > 0) for (var index = 0; index < mean.Length; index++) mean[index] /= (float)norm;
        return mean;
    }

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count != right.Count) return -1;
        double dot = 0, a = 0, b = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index]; a += left[index] * left[index]; b += right[index] * right[index];
        }
        return a == 0 || b == 0 ? -1 : dot / Math.Sqrt(a * b);
    }

    private static Sample CreateSample(long faceId, long personId, string label, string leakageGroup, float[] embedding,
        float detectionConfidence, float relativeArea)
    {
        var bucket = SHA256.HashData(Encoding.UTF8.GetBytes(leakageGroup))[0] % 20;
        var partition = bucket < 14 ? "train" : bucket < 17 ? "validation" : "test";
        var qualityScore = Math.Clamp(detectionConfidence * .65 + Math.Min(1, relativeArea / .08f) * .35, .05, 1);
        var qualityTier = qualityScore >= .8 ? "high" : qualityScore >= .55 ? "medium" : "low";
        return new(faceId, personId, label, leakageGroup, partition, qualityScore, qualityTier, embedding);
    }

    private static string Fingerprint(IEnumerable<Sample> samples)
    {
        var fingerprintText = string.Join('|', samples.OrderBy(value => value.FaceId).ThenBy(value => value.PersonId)
            .ThenBy(value => value.Label, StringComparer.Ordinal)
            .Select(value => $"{value.FaceId}:{value.PersonId}:{value.Label}:{value.LeakageGroup}:{value.Partition}:{value.QualityScore:F6}"));
        return "picer-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintText)))[..20].ToLowerInvariant();
    }

    private static string ExplainGateFailure(ArtifactResponse artifact)
    {
        var reasons = new List<string>();
        if (artifact.Metrics.FalseAcceptRate > .01) reasons.Add($"误识率 {artifact.Metrics.FalseAcceptRate:P2} 高于 1% 门限");
        if (artifact.Metrics.PositiveEvaluated < 20) reasons.Add($"独立正样本仅 {artifact.Metrics.PositiveEvaluated} 个（至少 20）");
        if (artifact.Metrics.NegativeEvaluated < 10) reasons.Add($"独立负样本仅 {artifact.Metrics.NegativeEvaluated} 个（至少 10）");
        if (artifact.People.Count < 2) reasons.Add("有效人物不足两位");
        if (artifact.Comparison is { NoFalseAcceptRegression: false }) reasons.Add("候选版本的误识风险高于当前基线");
        if (artifact.Comparison is { AcceptableFalseRejectRegression: false }) reasons.Add("候选版本的漏识率比当前基线退化超过 2%" );
        return "训练完成但未应用：" + string.Join("；", reasons.DefaultIfEmpty("评估门禁未通过")) + "。当前版本保持不变。";
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
    private sealed record Sample(long FaceId, long PersonId, string Label, string LeakageGroup, string Partition,
        double QualityScore, string QualityTier, float[] Embedding);
    private sealed record DatasetResponse(string Id);
    private sealed record JobResponse(string Id, string Status, string Stage, int Progress, string? ArtifactId, string? Error, string? LogsUrl = null);
    private sealed record MetricsResponse(double FalseAcceptRate, double FalseRejectRate,
        int PositiveEvaluated, int NegativeEvaluated);
    private sealed record PersonPolicyResponse(long PersonId, float[] Prototype, double Threshold);
    private sealed record RecipeReportResponse(int RawTrainingPositives, int SelectedTrainingPositives,
        int ExcludedSameSource, int ExcludedPersonCap, int PerPersonCap);
    private sealed record BaselineMetricsResponse(double FalseAcceptRate, double FalseRejectRate);
    private sealed record ComparisonResponse(double FalseAcceptRateDelta, double FalseRejectRateDelta,
        bool NoFalseAcceptRegression, bool AcceptableFalseRejectRegression);
    private sealed record ArtifactResponse(string Id, string BaseModelVersion, List<PersonPolicyResponse> People,
        MetricsResponse Metrics, RecipeReportResponse? RecipeReport = null,
        BaselineMetricsResponse? BaselineMetrics = null, ComparisonResponse? Comparison = null, string? Sha256 = null, string? Signature = null, string? SignatureAlgorithm = null);
}
