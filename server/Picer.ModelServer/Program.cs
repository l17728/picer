using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using Picer.ModelServer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ConcurrentDictionary<string, TrainingJob>>();
builder.Services.AddSingleton<ConcurrentDictionary<string, object>>();
builder.Services.AddSingleton<TrainingExecutor>();
builder.Services.AddSingleton<TrainingRecipeBuilder>();
builder.Services.AddSingleton<ITrainingAlgorithmAdapter, PrototypeCalibrationAdapter>();
builder.Services.AddSingleton<ArtifactSigner>();
builder.Services.AddSingleton<TrainingAttemptArchive>();
var app = builder.Build();

var apiKey = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY");
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api") || string.IsNullOrWhiteSpace(apiKey)) { await next(); return; }
    if (context.Request.Method == "GET" && (context.Request.Path.Value?.EndsWith("/jobs") == true || context.Request.Path.Value?.EndsWith("/models") == true))
    { await next(); return; }
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

app.MapGet("/", () => Results.Text("""
<!doctype html><html lang="zh"><head><meta charset="utf-8"><title>Picer 模型服务工作台</title>
<style>*{margin:0;padding:0;box-sizing:border-box}body{font-family:'Segoe UI',sans-serif;background:#f5f7fa;color:#1a1a2e;padding:20px}h1{font-size:24px;margin-bottom:4px;color:#16213e}h2{font-size:16px;margin:20px 0 10px;color:#0f3460}.sub{color:#666;font-size:13px;margin-bottom:16px}.card{background:#fff;border-radius:10px;padding:16px;margin-bottom:14px;box-shadow:0 1px 4px rgba(0,0,0,.08)}.badge{display:inline-block;padding:3px 10px;border-radius:12px;font-size:12px;font-weight:600}.badge.ok{background:#d4edda;color:#155724}.badge.err{background:#f8d7da;color:#721c24}.badge.run{background:#cce5ff;color:#004085}.badge.q{background:#fff3cd;color:#856404}table{width:100%;border-collapse:collapse;font-size:13px}th,td{padding:8px 12px;text-align:left;border-bottom:1px solid #eee}th{background:#f8f9fa;font-weight:600}button{padding:8px 16px;border:1px solid #0f3460;background:#0f3460;color:#fff;border-radius:6px;cursor:pointer;font-size:13px}button:hover{background:#1a4a7a}button.sm{padding:3px 8px;font-size:11px}textarea{padding:8px;border:1px solid #ddd;border-radius:6px;font-size:12px;width:100%;height:80px;font-family:monospace}input{padding:8px;border:1px solid #ddd;border-radius:6px;font-size:13px}.grid{display:grid;grid-template-columns:repeat(4,1fr);gap:12px}.metric{text-align:center}.metric .val{font-size:26px;font-weight:700;color:#0f3460}.metric .lbl{font-size:11px;color:#888;text-transform:uppercase}.bar{height:6px;background:#e9ecef;border-radius:3px;overflow:hidden}.bar-fill{height:100%;background:#007bff;transition:width .3s}#msg{font-size:12px;color:#666;margin-top:6px}</style></head><body>
<h1>Picer 模型服务工作台</h1>
<p class="sub">人脸训练 &middot; 原型校准 &middot; 制品管理 &middot; <a href="http://127.0.0.1:8090">返回统一管理台</a></p>
<div class="card"><div class="grid">
<div class="metric"><div class="val" id="m-jobs">-</div><div class="lbl">训练任务</div></div>
<div class="metric"><div class="val" id="m-acc">-</div><div class="lbl">验证准确率</div></div>
<div class="metric"><div class="val" id="m-far">-</div><div class="lbl">误识率</div></div>
<div class="metric"><div class="val"><span class="badge ok" id="hp">检查中</span></div><div class="lbl">服务状态</div></div>
</div></div>
<h2>提交训练</h2>
<div class="card">
<textarea id="samples" placeholder='[{"faceId":1,"personId":1,"embedding":[0.1,0.2,...],"split":"train"}]'></textarea>
<div style="margin-top:8px;display:flex;gap:8px"><input id="name" placeholder="数据集名称" style="flex:1"/><button onclick="go()">&#9654; 开始训练</button></div>
<div id="msg"></div>
</div>
<h2>训练任务</h2>
<div class="card"><table><thead><tr><th>ID</th><th>状态</th><th>阶段</th><th>进度</th><th>制品</th><th></th></tr></thead><tbody id="jobs"></tbody></table></div>
<h2>制品详情</h2>
<div class="card" id="art" style="color:#555;font-size:13px">提交训练后在此显示制品指标。</div>
<script>
const A='/api/face-training';
async function rf(){try{const h=await fetch('/health').then(r=>r.json());const b=document.getElementById('hp');b.className='badge '+(h.status==='ok'?'ok':'err');b.textContent=h.status==='ok'?'运行中':'异常';}catch(e){const b=document.getElementById('hp');b.className='badge err';b.textContent='连接失败';}
try{const js=await fetch(A+'/jobs').then(r=>r.json());document.getElementById('m-jobs').textContent=js.length;
document.getElementById('jobs').innerHTML=js.map(j=>`<tr><td title="${j.id}">${j.id.substring(0,14)}…</td><td><span class="badge ${j.status==='completed'?'ok':j.status==='failed'?'err':j.status==='running'?'run':'q'}">${j.status}</span></td><td>${j.stage||''}</td><td><div class="bar"><div class="bar-fill" style="width:${j.progress||0}%"></div></div></td><td>${j.artifactId?`<a href="#" onclick="sa('${j.artifactId}');return false" style="color:#0f3460">${j.artifactId}</a>`:'—'}</td><td>${(j.status==='queued'||j.status==='running')?`<button class="sm" onclick="cj('${j.id}')">取消</button>`:''}</td></tr>`).join('')||'<tr><td colspan="6" style="color:#999;text-align:center">暂无任务</td></tr>';}catch(e){}}
async function go(){const t=document.getElementById('samples').value.trim();if(!t){document.getElementById('msg').textContent='请输入样本JSON';return;}
try{const s=JSON.parse(t);const d=await fetch(A+'/datasets',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({snapshotId:document.getElementById('name').value||'manual',samples:s})}).then(r=>r.json());
const j=await fetch(A+'/jobs',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({datasetId:d.id,type:'prototype-calibration'})}).then(r=>r.json());
document.getElementById('msg').textContent='已提交：'+j.id;setTimeout(rf,500);}catch(e){document.getElementById('msg').textContent='失败：'+e.message;}}
async function cj(id){await fetch(A+'/jobs/'+id+'/cancel',{method:'POST'});rf();}
async function sa(id){const a=await fetch(A+'/artifacts/'+id).then(r=>r.ok?r.json():null);if(!a){alert('制品不存在');return;}
document.getElementById('m-acc').textContent=(a.metrics.validationAccuracy*100).toFixed(1)+'%';
document.getElementById('m-far').textContent=(a.metrics.falseAcceptRate*100).toFixed(1)+'%';
document.getElementById('art').innerHTML=`<h3 style="color:#0f3460">${a.artifactId}</h3><p>算法：${a.algorithm} | 创建：${new Date(a.createdUtc).toLocaleString()}</p><table style="margin-top:8px"><tr><th>指标</th><th>值</th><th>指标</th><th>值</th></tr><tr><td>总样本</td><td>${a.metrics.totalSamples}</td><td>人物数</td><td>${a.metrics.totalPersons}</td></tr><tr><td>训练准确率</td><td><b>${(a.metrics.trainAccuracy*100).toFixed(1)}%</b></td><td>验证准确率</td><td><b>${(a.metrics.validationAccuracy*100).toFixed(1)}%</b></td></tr><tr><td>误识率</td><td>${(a.metrics.falseAcceptRate*100).toFixed(2)}%</td><td>拒识率</td><td>${(a.metrics.falseRejectRate*100).toFixed(2)}%</td></tr><tr><td>同类相似度</td><td>${a.metrics.meanSimilaritySame.toFixed(3)}</td><td>异类相似度</td><td>${a.metrics.meanSimilarityDiff.toFixed(3)}</td></tr></table><h3 style="margin-top:12px;font-size:13px">人物原型（${a.prototypes.length}）</h3><table><tr><th>人物ID</th><th>样本数</th><th>阈值</th></tr>${a.prototypes.map(p=>`<tr><td>${p.personId}</td><td>${p.sampleCount}</td><td>${p.threshold.toFixed(3)}</td></tr>`).join('')}</table>`;}
setInterval(rf,3000);rf();
</script></body></html>
""", "text/html"));

app.MapGet("/api/models", () => Results.Ok(new[] {
    new { id = "picer-prototype-calibration", capability = "face-training", status = "ready" },
    new { id = "qwen3-vl-8b-instruct", capability = "multimodal", status = "configured" },
    new { id = "qwen3-asr-1.7b", capability = "language", status = "configured" },
    new { id = "sherpa-onnx-vits-zh-ll", capability = "tts", status = "pending" }
}));
app.MapPost("/api/ai/chat/completions", async (HttpRequest request) =>
{
    var endpoint = Environment.GetEnvironmentVariable("PICER_LANGUAGE_MODEL_ENDPOINT");
    if (string.IsNullOrWhiteSpace(endpoint)) return Results.Problem("统一模型平台尚未配置语言模型服务端点", statusCode: 503);
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    using var upstream = new HttpRequestMessage(HttpMethod.Post, endpoint);
    upstream.Content = new StreamContent(request.Body);
    upstream.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
    var key = Environment.GetEnvironmentVariable("PICER_LANGUAGE_MODEL_API_KEY");
    if (!string.IsNullOrWhiteSpace(key)) upstream.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
    var response = await client.SendAsync(upstream);
    return Results.Content(await response.Content.ReadAsStringAsync(), "application/json", statusCode: (int)response.StatusCode);
});
app.MapGet("/api/face-training/algorithms", (IEnumerable<ITrainingAlgorithmAdapter> adapters) =>
    Results.Ok(adapters.Select(a => new { id = a.Id, modelVersion = a.ModelVersion })));
app.MapGet("/api/face-training/signing-key", (ArtifactSigner signer) => Results.Ok(signer.GetInfo()));

app.MapPost("/api/face-training/datasets", (DatasetRequest request, ConcurrentDictionary<string, object> index) =>
{
    if (request.ProtocolVersion != "face-evidence.v1") return Results.BadRequest(new { error = "unsupported protocolVersion" });
    if (request.Samples?.Any(s => s.Embedding is null || s.Embedding.Length == 0) == true)
        return Results.BadRequest(new { error = "sample embedding is required" });
    var datasetId = "dataset-" + Guid.NewGuid().ToString("N");
    index[datasetId] = request;
    return Results.Ok(new { id = datasetId, snapshotId = request.SnapshotId, sampleCount = request.Samples?.Length ?? 0 });
});
app.MapGet("/api/face-training/archive", (TrainingAttemptArchive archive, int? limit) => Results.Ok(archive.ReadRecent(limit ?? 100)));
app.MapGet("/api/face-training/archive/summary", (TrainingAttemptArchive archive) => Results.Ok(new { summary = archive.BuildSummary() }));
app.MapPost("/api/face-training/recipe/preview", (DatasetRequest request, TrainingRecipeBuilder builder) =>
    Results.Ok(new { protocolVersion = request.ProtocolVersion, selections = builder.Build(request) }));
app.MapPost("/api/face-training/recipe/enrich", (VlEnrichmentRequest request) =>
    Results.Ok(new { protocolVersion = request.ProtocolVersion, attributes = request.Attributes ?? [] }));
app.MapPost("/api/face-training/quality-report", (DatasetRequest request) =>
{
    var samples = request.Samples ?? [];
    var groups = samples.Where(s => s.Evidence is not null)
        .GroupBy(s => s.Evidence!.GroupId ?? s.Evidence.ImageHash ?? $"face:{s.FaceId}", StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var leaked = groups.Where(g => g.Select(s => s.Split).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        .Select(g => g.Key).ToArray();
    var persons = samples.Select(s => s.PersonId).Distinct().Count();
    return Results.Ok(new { protocolVersion = request.ProtocolVersion, totalSamples = samples.Length,
        totalPersons = persons, trainSamples = samples.Count(s => s.Split == "train"),
        validationSamples = samples.Count(s => s.Split == "validation"), leakageGroups = leaked,
        passed = leaked.Length == 0 && persons > 0 });
});

app.MapPost("/api/face-training/jobs", (JobRequest request, ConcurrentDictionary<string, TrainingJob> jobs, TrainingExecutor executor, TrainingAttemptArchive archive, ConcurrentDictionary<string, object> datasets) =>
{
    var id = "job-" + Guid.NewGuid().ToString("N");
    var job = new TrainingJob(id, "queued", "queued", 0, null, null);
    jobs[id] = job;
    if (datasets.TryGetValue(request.DatasetId, out var datasetObj) && datasetObj is DatasetRequest dataset)
    {
        archive.Append(new TrainingAttemptRecord(request.DatasetId, dataset.SnapshotId, id, null, dataset.Attempt,
            dataset.ModelVersion ?? "sface", dataset.Samples?.Length ?? 0,
            dataset.Samples?.Select(sample => sample.PersonId).Distinct().Count() ?? 0,
            "queued", null, null, null, dataset.Attempt > 1 ? "glm-4.6v-flash" : null,
            dataset.Attempt > 1 ? "face-training-advisor.v1" : null, dataset.TrainingHints, DateTime.UtcNow));
        _ = Task.Run(async () => await executor.RunAsync(id, dataset, jobs));
    }
    else
        jobs[id] = job with { Status = "failed", Error = $"数据集不存在或格式错误：{request.DatasetId}" };
    return Results.Ok(jobs[id]);
});

app.MapGet("/api/face-training/jobs", (ConcurrentDictionary<string, TrainingJob> jobs) =>
    Results.Ok(jobs.Values.OrderByDescending(j => j.Id).Take(50).ToArray()));
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

public record DatasetRequest(string SnapshotId, string? SourceApp = null, string? ModelVersion = null,
    FaceSample[]? Samples = null, string ProtocolVersion = "face-evidence.v1",
    UserActionEvidence[]? Actions = null, MediaGroupEvidence[]? Groups = null,
    int Attempt = 1, string? TrainingHints = null);
public record FaceSample(long FaceId, long PersonId, float[] Embedding, string Split = "train",
    SampleEvidence? Evidence = null, VlAttributes? Attributes = null);
public record SampleEvidence(string Source, string? ImageHash = null, string? FileName = null,
    string? GroupId = null, string? ModelVersion = null, string? SelectionReason = null,
    string? ExclusionReason = null);
public record UserActionEvidence(long FaceId, long? PersonId, string Action, DateTime OccurredUtc,
    string Source = "user");
public record MediaGroupEvidence(string GroupId, string Kind, long[] MemberIds);
public record VlAttributes(string Pose, string Lighting, string[] Occlusion, string Expression,
    double FaceQuality, bool IsUsefulForTraining, string ModelVersion, string PromptVersion);
public record VlEnrichmentRequest(string ProtocolVersion = "face-evidence.v1", VlAttributes[]? Attributes = null);
public record JobRequest(string DatasetId, string Type);
public record TrainingJob(string Id, string Status, string Stage, int Progress, string? ArtifactId, string? Error);
public record TrainingArtifact(
    string ArtifactId, string Algorithm, string ModelVersion, DateTime CreatedUtc,
    IReadOnlyList<PersonPrototype> Prototypes, TrainingMetrics Metrics,
    string? Sha256 = null, string? Signature = null, string? SignatureAlgorithm = null, string? SigningKeyId = null);
public record PersonPrototype(long PersonId, float[] Centroid, double Threshold, int SampleCount);
public record TrainingMetrics(int TotalSamples, int TotalPersons, double TrainAccuracy, double ValidationAccuracy,
    double FalseAcceptRate, double FalseRejectRate, double MeanSimilaritySame, double MeanSimilarityDiff);

public sealed class TrainingExecutor
{
    private readonly ConcurrentDictionary<string, TrainingArtifact> _artifacts = new();
    private readonly ArtifactSigner _signer;
    private readonly TrainingAttemptArchive _archive;

    public TrainingExecutor(ArtifactSigner signer, TrainingAttemptArchive archive) { _signer = signer; _archive = archive; }

    public bool TryGetArtifact(string artifactId, out TrainingArtifact artifact) => _artifacts.TryGetValue(artifactId, out artifact!);

    public async Task RunAsync(string jobId, DatasetRequest dataset, ConcurrentDictionary<string, TrainingJob> jobs)
    {
        try
        {
            var workerPython = Environment.GetEnvironmentVariable("PICER_GPU_WORKER_PYTHON");
            var workerScript = Environment.GetEnvironmentVariable("PICER_GPU_WORKER_SCRIPT");
            if (!string.IsNullOrWhiteSpace(workerPython) && !string.IsNullOrWhiteSpace(workerScript))
            {
                jobs[jobId] = jobs[jobId] with { Status = "running", Stage = "gpu-worker", Progress = 5 };
                var payload = JsonSerializer.Serialize(new { modelVersion = dataset.ModelVersion ?? "sface", samples = dataset.Samples ?? [] });
                var start = new ProcessStartInfo(workerPython, $"\"{workerScript}\"") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var process = Process.Start(start) ?? throw new InvalidOperationException("GPU worker process could not start");
                await process.StandardInput.WriteAsync(payload); process.StandardInput.Close();
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                var timeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("PICER_GPU_WORKER_TIMEOUT_SECONDS"), out var configured)
                    ? Math.Clamp(configured, 30, 86400) : 1800;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* process may have exited */ }
                    throw new TimeoutException($"GPU worker timed out after {timeoutSeconds}s");
                }
                var output = await outputTask; var error = await errorTask;
                if (process.ExitCode != 0)
                {
                    var detail = string.IsNullOrWhiteSpace(error) ? output : error;
                    throw new InvalidOperationException($"GPU worker failed: {detail.Trim()}");
                }
                using var parsed = JsonDocument.Parse(output);
                if (parsed.RootElement.TryGetProperty("error", out var workerError)) throw new InvalidOperationException(workerError.GetString());
                var gpuArtifact = JsonSerializer.Deserialize<TrainingArtifact>(output, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("GPU worker returned invalid artifact");
                gpuArtifact = _signer.Sign(gpuArtifact);
                _artifacts[gpuArtifact.ArtifactId] = gpuArtifact;
                jobs[jobId] = jobs[jobId] with { Status = "completed", Stage = "gpu-worker-completed", Progress = 100, ArtifactId = gpuArtifact.ArtifactId };
                return;
            }
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
                // Later attempts deliberately relax the acceptance threshold. The
                // client may also provide LLM-generated hints; they are retained
                // as dataset evidence while the deterministic worker stays safe.
                var thresholdFactor = Math.Clamp(0.95 + Math.Min(Math.Max(dataset.Attempt - 1, 0), 8) * 0.005, 0.95, 0.99);
                var threshold = similarities.Count > 1 ? similarities.Min() * thresholdFactor : 0.5;
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
            var artifact = _signer.Sign(new TrainingArtifact(artifactId, "prototype-calibration", dataset.ModelVersion ?? "sface", DateTime.UtcNow, prototypes, metrics));
            _artifacts[artifactId] = artifact;

            jobs[jobId] = jobs[jobId] with { Status = "completed", Stage = "completed", Progress = 100, ArtifactId = artifactId };
            _archive.Append(new TrainingAttemptRecord("", "", jobId, artifactId, dataset.Attempt,
                dataset.ModelVersion ?? "sface", samples.Length, prototypes.Count, "completed",
                metrics.ValidationAccuracy, metrics.FalseAcceptRate, metrics.FalseRejectRate,
                dataset.Attempt > 1 ? "glm-4.6v-flash" : null,
                dataset.Attempt > 1 ? "face-training-advisor.v1" : null, dataset.TrainingHints, DateTime.UtcNow));
        }
        catch (Exception exception)
        {
            jobs[jobId] = jobs[jobId] with { Status = "failed", Error = exception.Message };
            _archive.Append(new TrainingAttemptRecord("", "", jobId, null, dataset.Attempt,
                dataset.ModelVersion ?? "sface", dataset.Samples?.Length ?? 0,
                dataset.Samples?.Select(sample => sample.PersonId).Distinct().Count() ?? 0,
                "failed", null, null, null, dataset.Attempt > 1 ? "glm-4.6v-flash" : null,
                dataset.Attempt > 1 ? "face-training-advisor.v1" : null, dataset.TrainingHints, DateTime.UtcNow));
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
