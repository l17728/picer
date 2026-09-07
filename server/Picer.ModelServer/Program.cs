using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ConcurrentDictionary<string, TrainingJob>>();
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "picer-model-server", version = "v1" }));
app.MapGet("/api/models", () => Results.Ok(new[] {
    new { id = "qwen3-vl-8b-instruct", capability = "multimodal", status = "configured" },
    new { id = "qwen3-asr-1.7b", capability = "language", status = "configured" },
    new { id = "sherpa-onnx-vits-zh-ll", capability = "tts", status = "pending" }
}));
app.MapPost("/api/face-training/datasets", (DatasetRequest request) => Results.Ok(new { id = "dataset-" + Guid.NewGuid().ToString("N"), snapshotId = request.SnapshotId }));
app.MapPost("/api/face-training/jobs", (JobRequest request, ConcurrentDictionary<string, TrainingJob> jobs) =>
{
    var id = "job-" + Guid.NewGuid().ToString("N"); jobs[id] = new(id, "queued", "queued", 0, null, null);
    return Results.Ok(jobs[id]);
});
app.MapGet("/api/face-training/jobs/{id}", (string id, ConcurrentDictionary<string, TrainingJob> jobs) => jobs.TryGetValue(id, out var job) ? Results.Ok(job) : Results.NotFound());
app.MapGet("/api/face-training/jobs/{id}/logs", (string id, ConcurrentDictionary<string, TrainingJob> jobs) => jobs.ContainsKey(id) ? Results.Ok("job=" + id + " status=" + jobs[id].Status) : Results.NotFound());
app.MapPost("/api/face-training/jobs/{id}/cancel", (string id, ConcurrentDictionary<string, TrainingJob> jobs) => jobs.TryGetValue(id, out var job) ? Results.Ok(job with { Status = "canceled", Stage = "canceled" }) : Results.NotFound());
app.MapGet("/api/face-training/artifacts/{id}", (string id) => Results.NotFound());
app.Run();

record DatasetRequest(string SnapshotId, string? SourceApp = null, string? ModelVersion = null, object[]? Samples = null);
record JobRequest(string DatasetId, string Type);
record TrainingJob(string Id, string Status, string Stage, int Progress, string? ArtifactId, string? Error);
