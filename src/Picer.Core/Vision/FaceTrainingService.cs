using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Picer.Core.Data;

namespace Picer.Core.Vision;

public sealed record FaceTrainingProgress(string Stage, int Percent, string Detail);
public sealed record FaceTrainingResult(string DatasetId, string JobId, string ArtifactId, int Samples,
    int People, double FalseAcceptRate, double FalseRejectRate, bool Applied, string Message);

public sealed class FaceTrainingService(
    VisionAnalysisRepository repository,
    HttpClient? client = null) : IDisposable
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    private readonly bool _ownsClient = client is null;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<FaceTrainingResult> SubmitAndApplyAsync(Uri endpoint,
        IProgress<FaceTrainingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new("snapshot", 2, "正在冻结人工确认与排他负样本"));
        var confirmed = repository.GetEmbeddingsForMatching()
            .Where(face => face.AssignmentState == "confirmed" && face.PersonId is not null).ToArray();
        var modelVersion = confirmed.GroupBy(face => face.ModelVersion).OrderByDescending(group => group.Count())
            .Select(group => group.Key).FirstOrDefault() ?? throw new InvalidOperationException("没有可训练的已确认人脸向量。");
        confirmed = confirmed.Where(face => face.ModelVersion == modelVersion).ToArray();
        var rejected = repository.GetRejectedFaceEmbeddings().Where(face => face.ModelVersion == modelVersion).ToArray();
        var people = confirmed.Select(face => face.PersonId!.Value).Distinct().ToArray();
        if (people.Length < 2) throw new InvalidOperationException("至少需要两位已有人工确认样本的人物。");
        var samples = confirmed.Select(face => new Sample(face.FaceId, face.PersonId!.Value, "positive",
                $"image:{face.ImageId}", face.Embedding))
            .Concat(rejected.Select(face => new Sample(face.FaceId, face.PersonId, "negative",
                $"image:{face.ImageId}", face.Embedding))).ToArray();
        var fingerprintText = string.Join('|', samples.OrderBy(value => value.FaceId)
            .Select(value => $"{value.FaceId}:{value.PersonId}:{value.Label}:{value.LeakageGroup}"));
        var snapshotId = "picer-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintText)))[..20].ToLowerInvariant();
        var root = new Uri(endpoint.ToString().TrimEnd('/') + "/");
        progress?.Report(new("upload", 10, $"正在提交 {samples.Length} 个脱敏向量样本"));
        using var datasetResponse = await _client.PostAsJsonAsync(new Uri(root, "datasets"),
            new { snapshotId, sourceApp = "picer", modelVersion, samples }, Json, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(datasetResponse, "提交训练快照", cancellationToken).ConfigureAwait(false);
        var dataset = await datasetResponse.Content.ReadFromJsonAsync<DatasetResponse>(Json, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("训练平台未返回数据集编号。");

        progress?.Report(new("queued", 18, "训练快照已接收，正在创建任务"));
        using var jobResponse = await _client.PostAsJsonAsync(new Uri(root, "jobs"),
            new { datasetId = dataset.Id, type = "prototype-calibration" }, Json, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(jobResponse, "创建训练任务", cancellationToken).ConfigureAwait(false);
        var job = await jobResponse.Content.ReadFromJsonAsync<JobResponse>(Json, cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidDataException("训练平台未返回任务编号。");
        var submittedJobId = job.Id;
        using var cancelRegistration = cancellationToken.Register(() =>
            _ = _client.PostAsync(new Uri(root, $"jobs/{submittedJobId}/cancel"), content: null, CancellationToken.None));
        while (job.Status is "queued" or "running" or "canceling")
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            job = await _client.GetFromJsonAsync<JobResponse>(new Uri(root, $"jobs/{job.Id}"), Json, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("训练平台返回了空任务状态。");
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
        if (artifact.People.Any(value => !people.Contains(value.PersonId))) throw new InvalidDataException("训练制品包含快照以外的人物编号。");
        var applied = artifact.Metrics.FalseAcceptRate <= 0.01 && artifact.Metrics.PositiveEvaluated >= 20 &&
                      artifact.Metrics.NegativeEvaluated >= 10 && artifact.People.Count >= 2;
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
            applied ? "训练完成并已应用；人工分类未改变。" : "训练完成，但评估未通过自动应用门禁。");
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

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
    private sealed record Sample(long FaceId, long PersonId, string Label, string LeakageGroup, float[] Embedding);
    private sealed record DatasetResponse(string Id);
    private sealed record JobResponse(string Id, string Status, string Stage, int Progress, string? ArtifactId, string? Error);
    private sealed record MetricsResponse(double FalseAcceptRate, double FalseRejectRate,
        int PositiveEvaluated, int NegativeEvaluated);
    private sealed record PersonPolicyResponse(long PersonId, float[] Prototype, double Threshold);
    private sealed record ArtifactResponse(string Id, string BaseModelVersion, List<PersonPolicyResponse> People, MetricsResponse Metrics);
}
