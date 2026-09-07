using System.Collections.Concurrent;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ConcurrentDictionary<string, TrainingJob>>();
builder.Services.AddSingleton<ConcurrentDictionary<string, object>>();
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
app.MapGet("/api/models", () => Results.Ok(new[] {
    new { id = "qwen3-vl-8b-instruct", capability = "multimodal", status = "configured" },
    new { id = "qwen3-asr-1.7b", capability = "language", status = "configured" },
    new { id = "sherpa-onnx-vits-zh-ll", capability = "tts", status = "pending" }
}));
app.MapGet("/api/index/v1/records", (string? cursor, int? limit, ConcurrentDictionary<string, object> index) =>
    Results.Ok(new { version = "index-v1", cursor, nextCursor = (string?)null, items = index.Take(Math.Clamp(limit ?? 200, 1, 1000)).Select(x => x.Value) }));
app.MapPut("/api/index/v1/records/{id}", (string id, object record, ConcurrentDictionary<string, object> index) => { index[id] = record; return Results.NoContent(); });
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
