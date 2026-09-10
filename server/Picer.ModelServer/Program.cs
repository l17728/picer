using System.Collections.Concurrent;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ConcurrentDictionary<string, TrainingJob>>();
builder.Services.AddSingleton<ConcurrentDictionary<string, object>>();
builder.Services.AddSingleton<TrainingExecutor>();
var app = builder.Build();

var apiKey = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY");
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api") || string.IsNullOrWhiteSpace(apiKey)) { await next(); return; }
    var header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(header[7..]), System.Text.Encoding.UTF8.GetBytes(apiKey)))
    { context.Response.StatusCode = StatusCodes.Status401Unauthorized; await context.Response.WriteAsJsonAsync(new { error = "unauthorized" }); return; }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "picer-model-server", version = "v1" }));
app.MapGet("/v1/models", () => Results.Ok(new
{
    object_ = "list",
    data = new object[]
    {
        new { id = "picer-prototype-calibration", owned_by = "picer", permission = Array.Empty<object>() },
        new { id = "picer-face-training", owned_by = "picer", permission = Array.Empty<object>() },
        new { id = "picer-index-service", owned_by = "picer", permission = Array.Empty<object>() }
    }
}));

app.MapGet("/api/models", () => Results.Ok(new[] {
    new { id = "picer-prototype-calibration", capability = "face-training", status = "ready" },
    new { id = "qwen3-vl-8b-instruct", capability = "multimodal", status = "configured" },
    new { id = "qwen3-asr-1.7b", capability = "language", status = "configured" },
    new { id = "sherpa-onnx-vits-zh-ll", capability = "tts", status = "pending" }
}));

app.MapPost("/api/face-training/datasets", (DatasetRequest request, ConcurrentDictionary<string, object> index) =>
{
    var datasetId = "dataset-" + Guid.NewGuid().ToString("N");
    index[datasetId] = request;
    return Results.Ok(new { id = datasetId, snapshotId = request.SnapshotId, sampleCount = request.Samples?.Length ?? 0 });
});

app.MapPost("/api/face-training/jobs", (JobRequest request, ConcurrentDictionary<string, TrainingJob> jobs, TrainingExecutor executor, ConcurrentDictionary<string, object> datasets) =>
{
    var id = "job-" + Guid.NewGuid().ToString("N");
    var job = new TrainingJob(id, "queued", "queued", 0, null, null);
    jobs[id] = job;
    if (datasets.TryGetValue(request.DatasetId, out var datasetObj) && datasetObj is DatasetRequest dataset)
        _ = Task.Run(async () => await executor.RunAsync(id, dataset, jobs));
    else
        jobs[id] = job with { Status = "failed", Error = $"数据集不存在或格式错误：{request.DatasetId}" };
    return Results.Ok(jobs[id]);
});

app.MapGet("/api/face-training/jobs/{id}", (string id, ConcurrentDictionary<string, TrainingJob> jobs) => jobs.TryGetValue(id, out var job) ? Results.Ok(job) : Results.NotFound());
app.MapGet("/api/face-training/jobs/{id}/logs", (string id, ConcurrentDictionary<string, TrainingJob> jobs) => jobs.TryGetValue(id, out var job) ? Results.Ok($"job={id} status={job.Status} stage={job.Stage} progress={job.Progress}%") : Results.NotFound());
app.MapPost("/api/face-training/jobs/{id}/cancel", (string id, ConcurrentDictionary<string, TrainingJob> jobs) =>
{
    if (!jobs.TryGetValue(id, out var job)) return Results.NotFound();
    jobs[id] = job with { Status = "canceled", Stage = "canceled" };
    return Results.Ok(jobs[id]);
});

app.MapGet("/api/face-training/artifacts/{id}", (string id, TrainingExecutor executor) =>
    executor.TryGetArtifact(id, out var artifact) ? Results.Ok(artifact) : Results.NotFound());

app.Run();

public record DatasetRequest(string SnapshotId, string? SourceApp = null, string? ModelVersion = null, FaceSample[]? Samples = null);
public record FaceSample(long FaceId, long PersonId, float[] Embedding, string Split = "train");
public record JobRequest(string DatasetId, string Type);
public record TrainingJob(string Id, string Status, string Stage, int Progress, string? ArtifactId, string? Error);
public record TrainingArtifact(
    string ArtifactId, string Algorithm, string ModelVersion, DateTime CreatedUtc,
    IReadOnlyList<PersonPrototype> Prototypes, TrainingMetrics Metrics);
public record PersonPrototype(long PersonId, float[] Centroid, double Threshold, int SampleCount);
public record TrainingMetrics(int TotalSamples, int TotalPersons, double TrainAccuracy, double ValidationAccuracy,
    double FalseAcceptRate, double FalseRejectRate, double MeanSimilaritySame, double MeanSimilarityDiff);

public sealed class TrainingExecutor
{
    private readonly ConcurrentDictionary<string, TrainingArtifact> _artifacts = new();

    public bool TryGetArtifact(string artifactId, out TrainingArtifact artifact) => _artifacts.TryGetValue(artifactId, out artifact!);

    public async Task RunAsync(string jobId, DatasetRequest dataset, ConcurrentDictionary<string, TrainingJob> jobs)
    {
        try
        {
            jobs[jobId] = jobs[jobId] with { Status = "running", Stage = "extracting", Progress = 5 };
            await Task.Delay(200);

            var samples = dataset.Samples ?? [];
            if (samples.Length < 2)
            {
                jobs[jobId] = jobs[jobId] with { Status = "failed", Error = "样本数不足（至少需要 2 张已确认人脸）" };
                return;
            }

            var trainSamples = samples.Where(s => s.Split == "train").ToList();
            var validationSamples = samples.Where(s => s.Split == "validation").ToList();
            if (trainSamples.Count == 0) trainSamples = samples.ToList();
            jobs[jobId] = jobs[jobId] with { Stage = "clustering", Progress = 25 };
            await Task.Delay(200);

            var prototypes = new List<PersonPrototype>();
            foreach (var personGroup in trainSamples.GroupBy(s => s.PersonId))
            {
                var embeddings = personGroup.Select(s => s.Embedding).ToList();
                var centroid = new float[embeddings[0].Length];
                foreach (var embedding in embeddings)
                    for (var i = 0; i < centroid.Length; i++)
                        centroid[i] += embedding[i];
                for (var i = 0; i < centroid.Length; i++)
                    centroid[i] /= embeddings.Count;

                var similarities = embeddings.Select(e => CosineSimilarity(e, centroid)).ToList();
                var threshold = similarities.Count > 1 ? similarities.Min() * 0.95 : 0.5;
                prototypes.Add(new PersonPrototype(personGroup.Key, centroid, Math.Max(0.3, threshold), embeddings.Count));
            }

            jobs[jobId] = jobs[jobId] with { Stage = "evaluating", Progress = 60 };
            await Task.Delay(200);

            int truePositive = 0, falsePositive = 0, falseNegative = 0, correct = 0, total = 0;
            double sumSame = 0, sumDiff = 0; int countSame = 0, countDiff = 0;
            var evalSamples = validationSamples.Count > 0 ? validationSamples : trainSamples;
            foreach (var sample in evalSamples)
            {
                total++;
                var bestProto = prototypes
                    .Select(p => (Proto: p, Sim: CosineSimilarity(sample.Embedding, p.Centroid)))
                    .OrderByDescending(x => x.Sim)
                    .FirstOrDefault();
                var predicted = bestProto.Sim >= bestProto.Proto.Threshold ? bestProto.Proto.PersonId : 0;
                if (predicted == sample.PersonId)
                {
                    correct++; truePositive++;
                    sumSame += bestProto.Sim; countSame++;
                }
                else if (predicted != 0)
                {
                    falsePositive++;
                    sumDiff += bestProto.Sim; countDiff++;
                }
                else
                {
                    falseNegative++;
                }
            }

            foreach (var sample in trainSamples)
            {
                var best = prototypes
                    .Select(p => (Proto: p, Sim: CosineSimilarity(sample.Embedding, p.Centroid)))
                    .OrderByDescending(x => x.Sim)
                    .First();
                if (best.Proto.PersonId == sample.PersonId && best.Sim >= best.Proto.Threshold) correct++;
            }
            var trainAccuracy = trainSamples.Count > 0 ? (double)trainSamples.Count(s =>
            {
                var best = prototypes.Select(p => (P: p, S: CosineSimilarity(s.Embedding, p.Centroid))).OrderByDescending(x => x.S).First();
                return best.P.PersonId == s.PersonId && best.S >= best.P.Threshold;
            }) / trainSamples.Count : 0;

            var far = total > 0 ? (double)falsePositive / total : 0;
            var frr = total > 0 ? (double)falseNegative / total : 0;
            var valAccuracy = total > 0 ? (double)correct / total : 0;

            jobs[jobId] = jobs[jobId] with { Stage = "packaging", Progress = 85 };
            await Task.Delay(200);

            var metrics = new TrainingMetrics(
                samples.Length, prototypes.Count,
                Math.Round(trainAccuracy, 4), Math.Round(valAccuracy, 4),
                Math.Round(far, 4), Math.Round(frr, 4),
                Math.Round(countSame > 0 ? sumSame / countSame : 0, 4),
                Math.Round(countDiff > 0 ? sumDiff / countDiff : 0, 4));

            var artifactId = "artifact-" + Guid.NewGuid().ToString("N")[..12];
            var artifact = new TrainingArtifact(artifactId, "prototype-calibration", dataset.ModelVersion ?? "sface", DateTime.UtcNow, prototypes, metrics);
            _artifacts[artifactId] = artifact;

            jobs[jobId] = jobs[jobId] with { Status = "completed", Stage = "completed", Progress = 100, ArtifactId = artifactId };
        }
        catch (Exception exception)
        {
            jobs[jobId] = jobs[jobId] with { Status = "failed", Error = exception.Message };
        }
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB) + 1e-10);
    }
}
