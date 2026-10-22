using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using Picer.ModelServer;

const string DefaultLanguageModelEndpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
const string DefaultLanguageModel = "glm-4.6v-flash";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 512L * 1024 * 1024);
builder.Services.AddSingleton<ConcurrentDictionary<string, TrainingJob>>();
builder.Services.AddSingleton<ConcurrentDictionary<string, object>>();
builder.Services.AddSingleton<TrainingExecutor>();
builder.Services.AddSingleton<TrainingRecipeBuilder>();
builder.Services.AddSingleton<ITrainingAlgorithmAdapter, PrototypeCalibrationAdapter>();
builder.Services.AddSingleton<ITrainingAlgorithmAdapter, ArcFaceAdapter>();
builder.Services.AddSingleton<ITrainingAlgorithmAdapter, AdaFaceAdapter>();
builder.Services.AddSingleton<ITrainingAlgorithmAdapter, InsightFaceAdapter>();
builder.Services.AddSingleton<ITrainingAlgorithmAdapter, ArcFacePaddleAdapter>();
builder.Services.AddSingleton<ITrainingAlgorithmAdapter, FaceXZooAdapter>();
builder.Services.AddSingleton<ArtifactSigner>();
builder.Services.AddSingleton<TrainingAttemptArchive>();
builder.Services.AddSingleton<ModelCandidateRankingStore>();
builder.Services.AddSingleton<EvaluationDatasetStore>();
builder.Services.AddSingleton<GpuEmbeddingService>();
builder.Services.AddSingleton<GpuTrainingWorkerHost>();
builder.Services.AddSingleton<ClipEmbeddingService>();
builder.Services.AddSingleton(_ => new ModelInferenceRelay(new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 8,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10), ConnectTimeout = TimeSpan.FromSeconds(10)
}) { Timeout = Timeout.InfiniteTimeSpan }));
var app = builder.Build();

// Restore the observable job list after a server restart. Terminal records are
// reconstructed from the append-only archive; in-flight work is explicitly
// marked recoverable instead of being silently reported as running.
var restoredArchive = app.Services.GetRequiredService<TrainingAttemptArchive>();
var restoredJobs = app.Services.GetRequiredService<ConcurrentDictionary<string, TrainingJob>>();
foreach (var record in restoredArchive.ReadRecent(1000).GroupBy(r => r.JobId).Select(g => g.First()))
{
    if (string.IsNullOrWhiteSpace(record.JobId)) continue;
    var status = record.Status is "completed" or "failed" or "canceled" ? record.Status : "recoverable";
    var error = status == "recoverable" ? "ModelServer 重启后任务未完成，可安全重试" : null;
    restoredJobs.TryAdd(record.JobId, new TrainingJob(record.JobId, status, status, status == "completed" ? 100 : 0,
        record.ArtifactId, error));
}

var apiKey = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY");
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api") || string.IsNullOrWhiteSpace(apiKey)) { await next(); return; }
    if (context.Request.Method == "GET" && (context.Request.Path.Value?.EndsWith("/jobs") == true || context.Request.Path.Value?.EndsWith("/models") == true
        || context.Request.Path.Value?.EndsWith("/model-rankings") == true || context.Request.Path.Value?.EndsWith("/model-rankings/rounds") == true))
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
<h2>提交人物识别校准任务</h2>
<div class="card">
<textarea id="samples" placeholder='[{"faceId":1,"personId":1,"embedding":[0.1,0.2,...],"split":"train"}]'></textarea>
<div style="margin-top:8px;display:flex;gap:8px"><input id="name" placeholder="数据集名称" style="flex:1"/><button onclick="go()">&#9654; 开始训练</button></div>
<div id="msg"></div>
</div>
<h2>校准任务</h2>
<div class="card"><table><thead><tr><th>ID</th><th>状态</th><th>阶段</th><th>进度</th><th>制品</th><th></th></tr></thead><tbody id="jobs"></tbody></table></div>
<h2>人物识别校准历史</h2>
<div class="card"><table><thead><tr><th>提交时间</th><th>模型</th><th>第几次</th><th>验证准确率</th><th>状态</th><th>是否采用</th></tr></thead><tbody id="attempts"><tr><td colspan="6">读取中…</td></tr></tbody></table></div>
<h2>制品详情</h2>
<div class="card" id="art" style="color:#555;font-size:13px">提交校准任务后在此显示制品指标。</div>
<h2>Model leaderboard</h2>
<div class="card"><p style="font-size:12px;color:#666;margin-bottom:8px">多模型基线评估由统一模型平台执行；客户端只负责提交不可变评估快照。无评估轮次的模型显示为“暂无数据”，不会被误认为准确率为 0。</p><div id="evalset" style="font-size:12px;color:#555;margin-bottom:8px">评估集读取中…</div><button onclick="baseline()">一键运行多模型基线</button><span id="baseline-msg" style="font-size:12px;color:#666;margin-left:8px"></span><table><thead><tr><th>Rank</th><th>Model</th><th>Composite score</th><th>Wins</th><th>Rounds</th><th>Best validation</th><th>Retained</th></tr></thead><tbody id="rankings"><tr><td colspan="7">Loading...</td></tr></tbody></table></div>
<script>
const A=location.pathname.startsWith('/picer')?'/picer/api/face-training':'/api/face-training';
async function rf(){try{const health=location.pathname.startsWith('/picer')?'/picer/health':'/health';const h=await fetch(health,{cache:'no-store'}).then(r=>r.json());const b=document.getElementById('hp');b.className='badge '+(h.status==='ok'?'ok':'err');b.textContent=h.status==='ok'?'运行中':'异常';}catch(e){const b=document.getElementById('hp');b.className='badge err';b.textContent='连接失败';}
try{const js=await fetch(A+'/jobs').then(r=>r.json());document.getElementById('m-jobs').textContent=js.length;
document.getElementById('jobs').innerHTML=js.map(j=>`<tr><td title="${j.id}">${j.id.substring(0,14)}…</td><td><span class="badge ${j.status==='completed'?'ok':j.status==='failed'?'err':j.status==='running'?'run':'q'}">${j.status}</span></td><td>${j.stage||''}</td><td><div class="bar"><div class="bar-fill" style="width:${j.progress||0}%"></div></div></td><td>${j.artifactId?`<a href="#" onclick="sa('${j.artifactId}');return false" style="color:#0f3460">${j.artifactId}</a>`:'—'}</td><td>${(j.status==='queued'||j.status==='running')?`<button class="sm" onclick="cj('${j.id}')">取消</button>`:''}</td></tr>`).join('')||'<tr><td colspan="6" style="color:#999;text-align:center">暂无任务</td></tr>';}catch(e){}}
async function go(){const t=document.getElementById('samples').value.trim();if(!t){document.getElementById('msg').textContent='请输入样本JSON';return;}
try{const s=JSON.parse(t);const d=await fetch(A+'/datasets',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({snapshotId:document.getElementById('name').value||'manual',samples:s})}).then(r=>r.json());
const j=await fetch(A+'/jobs',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({datasetId:d.id,type:'prototype-calibration'})}).then(r=>r.json());
document.getElementById('msg').textContent='已提交：'+j.id;setTimeout(rf,500);}catch(e){document.getElementById('msg').textContent='失败：'+e.message;}}
async function cj(id){await fetch(A+'/jobs/'+id+'/cancel',{method:'POST'});rf();}
async function rh(){try{const hs=await fetch(A+'/archive?limit=1000').then(r=>r.json());const by=new Map();hs.forEach(x=>{const key=x.jobId||x.artifactId||`${x.modelVersion||'unknown'}:${x.attempt||0}:${x.createdUtc}`;const old=by.get(key);if(!old||x.createdUtc>old.createdUtc)by.set(key,x);});document.getElementById('attempts').innerHTML=[...by.values()].sort((a,b)=>b.createdUtc.localeCompare(a.createdUtc)).map(x=>`<tr><td>${new Date(x.createdUtc).toLocaleString()}</td><td>${x.modelVersion||'—'}</td><td>${x.attempt||'—'}</td><td>${x.validationAccuracy==null?'—':(x.validationAccuracy*100).toFixed(1)+'%'}</td><td>${x.status||'—'}</td><td>${x.applied?'是':'否'}</td></tr>`).join('')||'<tr><td colspan="6">暂无训练历史</td></tr>';}catch(e){}}
async function sa(id){const a=await fetch(A+'/artifacts/'+id).then(r=>r.ok?r.json():null);if(!a){alert('制品不存在');return;}
document.getElementById('m-acc').textContent=(a.metrics.validationAccuracy*100).toFixed(1)+'%';
document.getElementById('m-far').textContent=(a.metrics.falseAcceptRate*100).toFixed(1)+'%';
document.getElementById('art').innerHTML=`<h3 style="color:#0f3460">${a.artifactId}</h3><p>算法：${a.algorithm} | 创建：${new Date(a.createdUtc).toLocaleString()}</p><table style="margin-top:8px"><tr><th>指标</th><th>值</th><th>指标</th><th>值</th></tr><tr><td>总样本</td><td>${a.metrics.totalSamples}</td><td>人物数</td><td>${a.metrics.totalPersons}</td></tr><tr><td>训练准确率</td><td><b>${(a.metrics.trainAccuracy*100).toFixed(1)}%</b></td><td>验证准确率</td><td><b>${(a.metrics.validationAccuracy*100).toFixed(1)}%</b></td></tr><tr><td>误识率</td><td>${(a.metrics.falseAcceptRate*100).toFixed(2)}%</td><td>拒识率</td><td>${(a.metrics.falseRejectRate*100).toFixed(2)}%</td></tr><tr><td>同类相似度</td><td>${a.metrics.meanSimilaritySame.toFixed(3)}</td><td>异类相似度</td><td>${a.metrics.meanSimilarityDiff.toFixed(3)}</td></tr></table><h3 style="margin-top:12px;font-size:13px">人物原型（${a.prototypes.length}）</h3><table><tr><th>人物ID</th><th>样本数</th><th>阈值</th></tr>${a.prototypes.map(p=>`<tr><td>${p.personId}</td><td>${p.sampleCount}</td><td>${p.threshold.toFixed(3)}</td></tr>`).join('')}</table>`;}
async function rr(){try{const data=await fetch(A+'/model-rankings?scenario=baseline').then(r=>r.json());const rows=data.candidates||[];document.getElementById('rankings').innerHTML=rows.map(x=>`<tr><td>${x.rank||'-'}</td><td>${x.modelVersion}</td><td>${x.rounds?((x.compositeScore*100).toFixed(2)+'%'):'暂无数据'}</td><td>${x.rounds?(x.wins||0):'-'}</td><td>${x.rounds||0}</td><td>${x.rounds?((x.bestValidationAccuracy*100).toFixed(1)+'%'):'暂无数据'}</td><td>${x.retained?'yes':'no'}</td></tr>`).join('')||'<tr><td colspan="7">No measured baseline yet</td></tr>';}catch(e){}}
async function baseline(){const m=document.getElementById('baseline-msg');m.textContent='正在查找最新独立评估快照…';try{const r=await fetch(A+'/model-rankings/baseline/one-click',{method:'POST'});const d=await r.json();if(!r.ok){m.textContent='未启动：'+(d.error||r.status);return;}m.textContent='已提交 '+(d.modelCount||0)+' 个模型，评估编号 '+d.evaluationId;rr();}catch(e){m.textContent='请求失败：'+e.message;}}
async function ed(){try{const r=await fetch(A+'/evaluation-dataset');const e=document.getElementById('evalset');if(!r.ok){e.textContent='尚未提交统一评估集';return;}const d=await r.json();e.innerHTML=`统一评估集：${d.snapshotId} · ${d.sampleCount} 张 · ${d.persons} 人 · Gallery ${d.gallery} · Validation ${d.validation} · Test ${d.test}`+(d.samples?.length?`<details style="margin-top:4px"><summary>查看/移除样本</summary>${d.samples.map(s=>`<span style="display:inline-block;margin:2px 6px 2px 0">face ${s.faceId} / person ${s.personId} / ${s.split} <button class="sm" onclick="delEval(${s.faceId})">移除</button></span>`).join('')}</details>`:'');}catch(e){}}
async function delEval(id){if(!confirm('从固定评估集移除该样本？'))return;await fetch(A+'/evaluation-dataset/'+id,{method:'DELETE'});ed();}
function renderHistory(){fetch(A+'/model-rankings/rounds?scenario=baseline').then(r=>r.json()).then(d=>{let h=document.getElementById('history-card');if(!h){h=document.createElement('div');h.id='history-card';h.className='card';document.getElementById('rankings').closest('table').after(h);}const rows=d.rounds||[];h.innerHTML='<h3>历史排名（每轮）</h3><table><thead><tr><th>评估轮次</th><th>模型</th><th>轮内排名</th><th>综合分</th><th>准确率</th><th>时间</th></tr></thead><tbody>'+(rows.map(x=>`<tr><td title="${x.evaluationId}">${x.evaluationId.substring(0,18)}…</td><td>${x.modelVersion}</td><td>${x.rank}</td><td>${(x.score*100).toFixed(2)}%</td><td>${(x.validationAccuracy*100).toFixed(1)}%</td><td>${new Date(x.createdUtc).toLocaleString()}</td></tr>`).join('')||'<tr><td colspan="6">暂无历史评估</td></tr>')+'</tbody></table>';}).catch(()=>{});}
setInterval(renderHistory,5000);renderHistory();setInterval(rf,3000);setInterval(rr,5000);setInterval(ed,5000);setInterval(rh,5000);rf();rr();ed();rh();
</script></body></html>
""", "text/html"));

app.MapGet("/api/models", () => Results.Ok(new[] {
    new { id = "picer-prototype-calibration", capability = "face-training", status = "ready" },
    new { id = "qwen3-vl-8b-instruct", capability = "multimodal", status = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PICER_VL_MODEL_ENDPOINT")) ? "optional-unconfigured" : "ready" },
    new { id = "qwen3-asr-1.7b", capability = "language", status = "configured" },
    new { id = "sherpa-onnx-vits-zh-ll", capability = "tts", status = "platform-managed" }
}));
app.MapGet("/api/ai/capabilities", () => Results.Ok(new
{
    language = new { enabled = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PICER_LANGUAGE_MODEL_API_KEY")), model = DefaultLanguageModel },
    vision = new { enabled = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PICER_VL_MODEL_ENDPOINT")), model = "qwen3-vl-8b-instruct", optionalForTraining = true }
}));
app.MapPost("/api/clip/embedding", async (ClipEmbeddingRequest request, ClipEmbeddingService clip, CancellationToken cancellationToken) =>
{
    try
    {
        var embedding = await clip.EncodeAsync(request, cancellationToken);
        return Results.Json(new { modelVersion = ClipEmbeddingService.Version, embedding });
    }
    catch (FormatException) { return Results.BadRequest(new { error = "clip_image_base64_invalid" }); }
    catch (InvalidDataException error) { return Results.BadRequest(new { error = error.Message }); }
});
app.MapPost("/api/clip/text-batch", async (ClipTextBatchRequest request, ClipEmbeddingService clip, CancellationToken cancellationToken) =>
{
    try
    {
        var texts = request.Texts?.Select(value => value?.Trim() ?? "").ToArray() ?? [];
        if (texts.Any(value => value.Length > 512))
            return Results.BadRequest(new { error = "clip_text_invalid" });
        var embeddings = await clip.EncodeTextsAsync(texts, cancellationToken);
        return Results.Json(new { modelVersion = ClipEmbeddingService.Version, embeddings });
    }
    catch (InvalidDataException error) { return Results.BadRequest(new { error = error.Message }); }
});
app.MapPost("/api/ai/chat/completions", async (HttpContext context, ModelInferenceRelay relay) =>
{
    var endpoint = Environment.GetEnvironmentVariable("PICER_LANGUAGE_MODEL_ENDPOINT") ?? DefaultLanguageModelEndpoint;
    if (string.IsNullOrWhiteSpace(endpoint)) return Results.Problem("统一模型平台尚未配置语言模型服务端点", statusCode: 503);
    var key = Environment.GetEnvironmentVariable("PICER_LANGUAGE_MODEL_API_KEY");
    await relay.InvokeAsync(context, new Uri(endpoint), key, TimeSpan.FromMinutes(5));
    return Results.Empty;
});
app.MapPost("/api/ai/vision/completions", async (HttpContext context, ModelInferenceRelay relay) =>
{
    var endpoint = Environment.GetEnvironmentVariable("PICER_VL_MODEL_ENDPOINT");
    if (string.IsNullOrWhiteSpace(endpoint)) return Results.Problem("统一模型平台尚未配置本地 VL 服务端点", statusCode: 503);
    var key = Environment.GetEnvironmentVariable("PICER_VL_MODEL_API_KEY");
    await relay.InvokeAsync(context, new Uri(endpoint), key, TimeSpan.FromMinutes(10));
    return Results.Empty;
});
app.MapGet("/api/face-training/runtime", async (GpuEmbeddingService worker, CancellationToken ct) =>
    Results.Json(await worker.GetRuntimeAsync(ct)));
app.MapGet("/api/face-training/algorithms", (IEnumerable<ITrainingAlgorithmAdapter> adapters) =>
    Results.Ok(adapters.Select(a => new { id = a.Id, modelVersion = a.ModelVersion, name = a.DisplayName,
        available = a.IsAvailable, embeddingVersion = a.ModelVersion })));
app.MapGet("/api/face-training/model-rankings", (HttpRequest http, IEnumerable<ITrainingAlgorithmAdapter> adapters, ModelCandidateRankingStore rankings) =>
{
    var scenario = http.Query["scenario"].FirstOrDefault();
    var candidates = rankings.Rank(adapters, scenario);
    var measuredRounds = candidates.Select(x => x.Rounds).DefaultIfEmpty().Max();
    var singleModel = candidates.FirstOrDefault(x => x.SingleModelSelected)?.ModelVersion;
    return Results.Ok(new { policy = new { maxRounds = 20, iterationsPerRound = 9, retainedModels = 3,
        scenarioAware = true, trainingSelection = "top3", singleModelDecisionAfterRounds = 20 },
        scenario, totalRounds = measuredRounds, singleModelDecision = singleModel,
        candidates = candidates.Select(x => new { x.ModelVersion, x.AlgorithmId, x.Available, x.Rounds,
            x.PassedRounds, x.BestValidationAccuracy, x.BestFar, x.BestFrr, x.Retained, role = x.Retained ? "primary" : "backup",
            x.LastArtifactId, x.Rank, x.Wins, x.CompositeScore, x.LastRound }) });
});
app.MapGet("/api/face-training/model-rankings/rounds", (HttpRequest http, ModelCandidateRankingStore rankings) =>
{
    var scenario = http.Query["scenario"].FirstOrDefault();
    return Results.Ok(new { scenario, rounds = rankings.Rounds(scenario) });
});
app.MapGet("/api/face-training/model-ensemble", (HttpRequest http, IEnumerable<ITrainingAlgorithmAdapter> adapters, ModelCandidateRankingStore rankings) =>
{
    var scenario = http.Query["scenario"].FirstOrDefault();
    var ranked = rankings.Rank(adapters, scenario);
    return Results.Ok(new { scenario, strategy = "weighted-consensus-with-primary",
        members = ModelEnsemblePolicy.Build(ranked) });
});
app.MapPost("/api/face-training/model-ensemble/decide", (ModelEnsembleDecisionRequest request) =>
{
    var members = request.Members ?? [];
    var scores = request.Scores ?? [];
    return Results.Ok(ModelEnsemblePolicy.Decide(scores, members));
});
app.MapGet("/api/face-training/signing-key", (ArtifactSigner signer) => Results.Ok(signer.GetInfo()));

app.MapPost("/api/face-inference", async (FaceInferenceRequest request, IEnumerable<ITrainingAlgorithmAdapter> adapters,
    GpuEmbeddingService embeddingService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.ImageBase64) || request.ImageBase64.Length > 16 * 1024 * 1024)
        return Results.BadRequest(new { error = "imageBase64_required_or_too_large" });
    var adapter = adapters.FirstOrDefault(value => string.Equals(value.ModelVersion, request.ModelVersion, StringComparison.OrdinalIgnoreCase));
    if (adapter is null || !adapter.IsAvailable)
        return Results.BadRequest(new { error = "face_model_not_deployed" });
    try
    {
        var result = await embeddingService.DetectFacesAsync(request.ModelVersion, [request.ImageBase64], cancellationToken);
        return Results.Json(result);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or JsonException)
    {
        return Results.Problem(exception.Message, statusCode: 502);
    }
});

app.MapPost("/api/face-training/embeddings", async (EmbeddingRequest request, IEnumerable<ITrainingAlgorithmAdapter> adapters, GpuEmbeddingService embeddingService, CancellationToken cancellationToken) =>
{
    if (request.Images is null || request.Images.Length == 0)
        return Results.BadRequest(new { error = "at least one face crop is required" });
    var adapter = adapters.FirstOrDefault(value => string.Equals(value.ModelVersion, request.ModelVersion, StringComparison.OrdinalIgnoreCase));
    if (adapter is null || !adapter.IsAvailable)
        return Results.BadRequest(new { error = $"model {request.ModelVersion} is not deployed" });
    try
    {
        // GpuEmbeddingService owns one persistent worker and serializes GPU
        // access.  Spawning one CUDA process per request made concurrent
        // vector backfills exhaust VRAM and caused nondeterministic failures.
        var embeddings = await embeddingService.GenerateAsync(request.ModelVersion, request.Images, cancellationToken);
        return Results.Json(new { modelVersion = request.ModelVersion, embeddings });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
    {
        return Results.Problem(exception.Message, statusCode: 502);
    }
});

app.MapPost("/api/face-training/datasets", (DatasetRequest request, ConcurrentDictionary<string, object> index, EvaluationDatasetStore evaluation,
    IEnumerable<ITrainingAlgorithmAdapter> adapters, ModelCandidateRankingStore rankings) =>
{
    if (request.ProtocolVersion != "face-evidence.v1") return Results.BadRequest(new { error = "unsupported protocolVersion" });
    var samples = request.Samples ?? [];
    if (samples.Length == 0) return Results.BadRequest(new { error = "at least one sample is required" });
    if (string.Equals(request.Scenario, "baseline", StringComparison.OrdinalIgnoreCase))
    {
        if (samples.Any(sample => string.IsNullOrWhiteSpace(sample.FaceImageBase64)) ||
            samples.Count(sample => sample.Split is "train" or "gallery") == 0 ||
            samples.Count(sample => sample.Split == "test") == 0 ||
            samples.Select(sample => sample.PersonId).Distinct().Count() < 2)
            return Results.BadRequest(new { error = "统一基线评估集必须包含完整人脸裁剪图、Gallery 和 Query，并至少包含两个人物" });
        index["evaluation-" + request.SnapshotId] = request;
        evaluation.Replace(request);
        return Results.Ok(new { id = "evaluation-" + request.SnapshotId, snapshotId = request.SnapshotId, sampleCount = samples.Length, scenario = "baseline" });
    }
    var modelVersion = request.ModelVersion ?? "sface";
    var baselineCandidates = rankings.Rank(adapters, "baseline");
    if (baselineCandidates.Any(candidate => candidate.Rounds > 0) &&
        !baselineCandidates.Any(candidate => candidate.Retained && string.Equals(candidate.ModelVersion, modelVersion, StringComparison.OrdinalIgnoreCase)))
        return Results.Conflict(new { error = $"模型 {modelVersion} 当前为备用模型，仅 Top3 主力模型允许提交训练", modelVersion, role = "backup" });
    var imageOnly = string.Equals(modelVersion, "insightface-arcface", StringComparison.OrdinalIgnoreCase);
    if (samples.Any(s => (s.Embedding is null || s.Embedding.Length == 0) &&
                         (!imageOnly || string.IsNullOrWhiteSpace(s.FaceImageBase64))))
        return Results.BadRequest(new { error = imageOnly ? "sample requires embedding or faceImageBase64" : "sample embedding is required" });
    var dimensions = samples.Where(s => s.Embedding is { Length: > 0 }).Select(s => s.Embedding!.Length).Distinct().ToArray();
    if (!imageOnly && dimensions.Length != 1) return Results.BadRequest(new { error = "embedding dimensions must match" });
    if (imageOnly && dimensions.Length > 1) return Results.BadRequest(new { error = "embedding dimensions must match" });
    if (request.SelectedSampleCount > 0 && request.SelectedSampleCount != samples.Length)
        return Results.BadRequest(new { error = "selectedSampleCount does not match samples" });
    var leakage = samples
        .GroupBy(s => s.Evidence?.GroupId ?? s.Evidence?.ImageHash ?? $"face:{s.FaceId}", StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Select(s => s.Split).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        .Select(g => g.Key).Take(20).ToArray();
    if (leakage.Length > 0) return Results.BadRequest(new { error = "data leakage group crosses splits", groups = leakage });
    var evidenceVersions = samples.Select(s => s.Evidence?.ModelVersion).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if (evidenceVersions.Length > 1 || evidenceVersions.Any(v => !string.Equals(v, request.ModelVersion ?? "sface", StringComparison.OrdinalIgnoreCase)))
        return Results.BadRequest(new { error = "embedding model version mismatch" });
    if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
    {
        var existing = index.FirstOrDefault(pair => pair.Value is DatasetRequest d &&
            string.Equals(d.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(existing.Key))
            return Results.Ok(new { id = existing.Key, snapshotId = request.SnapshotId, sampleCount = request.Samples?.Length ?? 0, reused = true });
    }
    var datasetId = "dataset-" + Guid.NewGuid().ToString("N");
    // VL attributes are optional enrichment. Their absence never blocks the
    // vector-training path; the recipe builder can continue with embeddings.
    index[datasetId] = request;
    if (string.Equals(request.Scenario, "baseline", StringComparison.OrdinalIgnoreCase)) evaluation.Replace(request);
    return Results.Ok(new { id = datasetId, snapshotId = request.SnapshotId, sampleCount = request.Samples?.Length ?? 0 });
});
app.MapGet("/api/face-training/evaluation-dataset", (EvaluationDatasetStore store) =>
{
    var dataset = store.Get();
    return dataset is null ? Results.NotFound(new { error = "尚未提交统一评估集" }) : Results.Ok(new
    {
        snapshotId = dataset.SnapshotId, sampleCount = dataset.Samples?.Length ?? 0,
        persons = dataset.Samples?.Select(sample => sample.PersonId).Distinct().Count() ?? 0,
        gallery = dataset.Samples?.Count(sample => sample.Split is "train" or "gallery") ?? 0,
        validation = dataset.Samples?.Count(sample => sample.Split == "validation") ?? 0,
        test = dataset.Samples?.Count(sample => sample.Split == "test") ?? 0,
        samples = dataset.Samples?.Select(sample => new { sample.FaceId, sample.PersonId, sample.Split, groupId = sample.Evidence?.GroupId }).ToArray()
    });
});
app.MapDelete("/api/face-training/evaluation-dataset/{faceId:long}", (long faceId, EvaluationDatasetStore store) =>
    store.Remove(faceId) ? Results.Ok(new { removed = faceId }) : Results.NotFound(new { error = "评估集样本不存在" }));
app.MapPost("/api/face-training/model-rankings/baseline/one-click", async (
    IEnumerable<ITrainingAlgorithmAdapter> adapters, ConcurrentDictionary<string, object> datasets,
    EvaluationDatasetStore evaluation, GpuEmbeddingService embeddingService, ModelCandidateRankingStore rankings) =>
{
    var source = evaluation.Get();
    source ??= datasets.Values.OfType<DatasetRequest>()
        .Where(value => value.Samples is { Length: > 1 } &&
            value.Samples.All(sample => !string.IsNullOrWhiteSpace(sample.FaceImageBase64)) &&
            value.Samples.Any(sample => string.Equals(sample.Split, "test", StringComparison.OrdinalIgnoreCase)) &&
            value.Samples.Select(sample => sample.PersonId).Distinct().Count() >= 2)
        .LastOrDefault();
    if (source is null)
        return Results.BadRequest(new { error = "平台尚未收到可用于基线的独立评估快照；请先提交含 faceImageBase64 的 train/validation 数据集" });
    var sourceSamples = source.Samples!;
    var candidates = adapters.Where(adapter => adapter.IsAvailable).ToArray();
    if (candidates.Length == 0) return Results.Problem("no requested face model is available", statusCode: 503);
    var evaluationId = $"baseline-{source.SnapshotId}-{Guid.NewGuid():N}";
    var results = new List<object>();
    try
    {
        foreach (var adapter in candidates)
        {
            var images = sourceSamples.Select(sample => sample.FaceImageBase64!).ToArray();
            var vectors = await embeddingService.GenerateAsync(adapter.ModelVersion, images);
            if (vectors.Count != sourceSamples.Length) return Results.BadRequest(new { error = $"{adapter.ModelVersion} 向量数量与统一评估集不一致" });
            var modelSamples = sourceSamples.Select((sample, index) => sample with
            {
                Embedding = vectors[index], Evidence = sample.Evidence is null ? null : sample.Evidence with { ModelVersion = adapter.ModelVersion }
            }).ToArray();
            var metrics = RawFaceBaselineEvaluator.Evaluate(modelSamples);
            results.Add(new { modelVersion = adapter.ModelVersion, metrics });
            rankings.Append(new ModelCandidateRound(adapter.ModelVersion, "raw-baseline", 0, metrics.Accuracy,
                metrics.FalseAcceptRate, metrics.FalseRejectRate, true, null, DateTime.UtcNow, "baseline", evaluationId));
        }
    }
    catch (Exception exception)
    {
        return Results.Problem($"基线推理失败：{exception.Message}", statusCode: 502);
    }
    return Results.Ok(new { evaluationId, modelCount = results.Count, results, jobs = Array.Empty<object>(), mode = "raw-inference" });
});
app.MapPost("/api/face-training/model-rankings/baseline", (BaselineRequest request,
    IEnumerable<ITrainingAlgorithmAdapter> adapters, ConcurrentDictionary<string, TrainingJob> jobs,
    TrainingExecutor executor, ConcurrentDictionary<string, object> datasets, ModelCandidateRankingStore rankings) =>
{
    var source = request.Dataset;
    var supplied = request.Datasets;
    var candidates = adapters.Where(adapter => adapter.IsAvailable &&
        (supplied is null || supplied.ContainsKey(adapter.ModelVersion))).ToArray();
    if (candidates.Length == 0) return Results.Problem("no requested face model is available", statusCode: 503);
    var plans = new List<(ITrainingAlgorithmAdapter Adapter, DatasetRequest Dataset)>();
    var evaluationId = request.EvaluationId ?? $"baseline-{Guid.NewGuid():N}";
    if (supplied is not null)
    {
        var evaluationResults = new List<object>();
        foreach (var adapter in candidates)
        {
            var modelSamples = supplied[adapter.ModelVersion];
            if (modelSamples.Length < 2 || modelSamples.Count(sample => sample.Split == "validation") == 0 ||
                modelSamples.Select(sample => sample.PersonId).Distinct().Count() < 2)
                return Results.BadRequest(new { error = $"{adapter.ModelVersion} requires train/validation samples from at least two people" });
            var dimensions = modelSamples.Where(sample => sample.Embedding is { Length: > 0 })
                .Select(sample => sample.Embedding!.Length).Distinct().ToArray();
            if (modelSamples.Any(sample => sample.Embedding is null || sample.Embedding.Length == 0) || dimensions.Length != 1)
                return Results.BadRequest(new { error = $"{adapter.ModelVersion} has missing or inconsistent vectors" });
            var leaked = modelSamples.Where(sample => sample.Evidence is not null)
                .GroupBy(sample => sample.Evidence!.GroupId ?? sample.Evidence.ImageHash ?? $"face:{sample.FaceId}", StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Select(sample => sample.Split).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
            if (leaked) return Results.BadRequest(new { error = $"{adapter.ModelVersion} evaluation groups cross train/validation splits" });
            var metrics = RawFaceBaselineEvaluator.Evaluate(modelSamples);
            rankings.Append(new ModelCandidateRound(adapter.ModelVersion, "raw-baseline", 0,
                metrics.Accuracy, metrics.FalseAcceptRate, metrics.FalseRejectRate,
                true, null, DateTime.UtcNow, "baseline", evaluationId));
            evaluationResults.Add(new { modelVersion = adapter.ModelVersion, metrics });
        }
        return Results.Ok(new { evaluationId, modelCount = evaluationResults.Count, results = evaluationResults, jobs = Array.Empty<object>(), mode = "raw-inference" });
    }
    else
    {
        var samples = source?.Samples ?? [];
        if (samples.Length < 2 || samples.Any(sample => string.IsNullOrWhiteSpace(sample.FaceImageBase64)) ||
            samples.Count(sample => sample.Split == "validation") == 0 || samples.Select(sample => sample.PersonId).Distinct().Count() < 2)
            return Results.BadRequest(new { error = "baseline requires an independent validation set with faceImageBase64 samples from at least two people" });
        foreach (var adapter in candidates)
        {
            var modelSamples = samples.Select(sample => sample with { Embedding = null,
                Evidence = sample.Evidence is null ? null : sample.Evidence with { ModelVersion = adapter.ModelVersion } }).ToArray();
            plans.Add((adapter, source! with { ModelVersion = adapter.ModelVersion, Samples = modelSamples,
                IdempotencyKey = null, EvaluationId = evaluationId, RecipeReason = "baseline evaluation snapshot" }));
        }
    }
    var submitted = new List<TrainingJob>();
    foreach (var plan in plans)
    {
        var adapter = plan.Adapter;
        var dataset = plan.Dataset;
        var datasetId = "dataset-" + Guid.NewGuid().ToString("N");
        datasets[datasetId] = dataset;
        var jobId = "job-" + Guid.NewGuid().ToString("N");
        var job = new TrainingJob(jobId, "queued", "queued", 0, null, null);
        jobs[jobId] = job;
        submitted.Add(job);
        _ = Task.Run(async () => await executor.RunAsync(jobId, dataset, jobs));
    }
    return Results.Ok(new { evaluationId, modelCount = plans.Count, jobs = submitted });
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
    var duplicateHashes = samples.Where(s => !string.IsNullOrWhiteSpace(s.Evidence?.ImageHash))
        .GroupBy(s => s.Evidence!.ImageHash!, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Select(s => s.FaceId).Distinct().Count() > 1)
        .Select(g => new { hash = g.Key, faceIds = g.Select(s => s.FaceId).Distinct().ToArray(), count = g.Count() })
        .ToArray();
    var missingEmbeddings = samples.Where(s => s.Embedding is null || s.Embedding.Length == 0)
        .Select(s => s.FaceId).Distinct().ToArray();
    var persons = samples.Select(s => s.PersonId).Distinct().Count();
    var positive = samples.Where(s => string.Equals(s.Label, "positive", StringComparison.OrdinalIgnoreCase)).ToArray();
    var negative = samples.Where(s => string.Equals(s.Label, "negative", StringComparison.OrdinalIgnoreCase)).ToArray();
    var warnings = new List<string>();
    if (persons < 2) warnings.Add("至少需要两个已标注人物");
    if (samples.Count(s => s.Split.Equals("validation", StringComparison.OrdinalIgnoreCase)) == 0)
        warnings.Add("缺少 validation 分区");
    if (samples.Count(s => s.Split.Equals("test", StringComparison.OrdinalIgnoreCase)) == 0)
        warnings.Add("缺少独立 test 分区");
    if (missingEmbeddings.Length > 0) warnings.Add($"存在 {missingEmbeddings.Length} 条缺失向量");
    if (leaked.Length > 0) warnings.Add($"存在 {leaked.Length} 个跨分区泄漏组");
    if (negative.Length == 0 && (request.NegativeSamples?.Length ?? 0) == 0)
        warnings.Add("缺少排他/未知人物负样本");
    foreach (var person in positive.GroupBy(s => s.PersonId))
        if (person.Count() < 2) warnings.Add($"人物 {person.Key} 正样本不足 2 条");
    var dated = samples.Select(s => s.Attributes).Where(a => a is not null).Select(a => a!).ToArray();
    var attributeVariants = dated.Select(a => $"{a.Pose}|{a.Lighting}|{a.Expression}").Distinct(StringComparer.OrdinalIgnoreCase).Count();
    if (dated.Length > 0 && attributeVariants < 2) warnings.Add("姿态/光照属性覆盖不足，无法证明困难样本泛化");
    var passed = warnings.Count == 0;
    return Results.Ok(new { protocolVersion = request.ProtocolVersion, totalSamples = samples.Length,
        totalPersons = persons, trainSamples = samples.Count(s => s.Split == "train"),
        validationSamples = samples.Count(s => s.Split == "validation"),
        testSamples = samples.Count(s => s.Split == "test"), positiveSamples = positive.Length,
        negativeSamples = negative.Length + (request.NegativeSamples?.Length ?? 0),
        leakageGroups = leaked, duplicateImageHashes = duplicateHashes,
        missingEmbeddingFaceIds = missingEmbeddings, attributeCoverage = dated.Length,
        attributeVariantCount = attributeVariants, warnings, passed });
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
            dataset.Attempt > 1 ? "face-training-advisor.v1" : null, dataset.TrainingHints, DateTime.UtcNow,
            dataset.RecipeFingerprint, dataset.RecipeReason, dataset.SelectedSampleCount, dataset.ExcludedSampleCount,
            false, dataset.Scenario));
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
app.MapPost("/api/face-training/artifacts/{id}/applied", (string id, TrainingAttemptArchive archive) =>
    archive.MarkApplied(id) ? Results.Ok(new { artifactId = id, applied = true }) : Results.NotFound());

app.Run();

public record DatasetRequest(string SnapshotId, string? SourceApp = null, string? ModelVersion = null,
    FaceSample[]? Samples = null, string ProtocolVersion = "face-evidence.v1",
    UserActionEvidence[]? Actions = null, MediaGroupEvidence[]? Groups = null, FaceNegativeSample[]? NegativeSamples = null,
    int Attempt = 1, string? TrainingHints = null, string? RecipeFingerprint = null,
    string? RecipeReason = null, int SelectedSampleCount = 0, int ExcludedSampleCount = 0,
    string? IdempotencyKey = null, string? Scenario = null, string? EvaluationId = null);
public record EmbeddingRequest(string ModelVersion, string[] Images);
public record FaceInferenceRequest(string ModelVersion, string ImageBase64);
public record FaceSample(long FaceId, long PersonId, float[]? Embedding = null, string Split = "train",
    SampleEvidence? Evidence = null, VlAttributes? Attributes = null, string Label = "positive",
    string? FaceImageBase64 = null);
public record FaceNegativeSample(long FaceId, long TargetPersonId, float[] Embedding,
    string Split = "validation", string Kind = "exclusive");
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
public record BaselineRequest(DatasetRequest? Dataset = null,
    Dictionary<string, FaceSample[]>? Datasets = null, string? SnapshotId = null,
    string? EvaluationId = null);
public record ModelEnsembleDecisionRequest(ModelEnsembleMember[]? Members = null, ModelEnsembleScore[]? Scores = null);
public record TrainingJob(string Id, string Status, string Stage, int Progress, string? ArtifactId, string? Error);
public record TrainingArtifact(
    string ArtifactId, string Algorithm, string ModelVersion, DateTime CreatedUtc,
    IReadOnlyList<PersonPrototype> Prototypes, TrainingMetrics Metrics,
    string? Sha256 = null, string? Signature = null, string? SignatureAlgorithm = null, string? SigningKeyId = null,
    TrainingDiagnostics? Diagnostics = null);
public record PersonPrototype(long PersonId, float[] Centroid, double Threshold, int SampleCount);
public record TrainingMetrics(int TotalSamples, int TotalPersons, double TrainAccuracy, double ValidationAccuracy,
    double FalseAcceptRate, double FalseRejectRate, double MeanSimilaritySame, double MeanSimilarityDiff);
public record TrainingDiagnostics(string RecommendedAction, double ThresholdSweepBestAccuracy,
    double ThresholdSweepBestScale, double ThresholdSweepBestFar, double ThresholdSweepBestFrr,
    double CurrentAccuracy, string Explanation);
public static class CalibrationGate
{
    public static bool Passes(TrainingMetrics metrics) =>
        metrics.ValidationAccuracy >= 0.90 &&
        metrics.FalseAcceptRate <= 0.01 &&
        metrics.FalseRejectRate <= 0.10;
}

public sealed class TrainingExecutor
{
    private readonly ConcurrentDictionary<string, TrainingArtifact> _artifacts = new();
    private readonly string _artifactRoot;
    private readonly ArtifactSigner _signer;
    private readonly TrainingAttemptArchive _archive;
    private readonly ModelCandidateRankingStore _rankings;
    private readonly GpuTrainingWorkerHost _gpuWorker;

    public TrainingExecutor(ArtifactSigner signer, TrainingAttemptArchive archive, ModelCandidateRankingStore rankings, GpuTrainingWorkerHost gpuWorker)
    {
        _signer = signer; _archive = archive; _rankings = rankings; _gpuWorker = gpuWorker;
        var root = Environment.GetEnvironmentVariable("PICER_TRAINING_ARCHIVE_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "training-archive");
        _artifactRoot = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(_artifactRoot);
        foreach (var path in Directory.EnumerateFiles(_artifactRoot, "artifact-*.json"))
        {
            try
            {
                var artifact = JsonSerializer.Deserialize<TrainingArtifact>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (artifact is not null) _artifacts[artifact.ArtifactId] = artifact;
            }
            catch { /* ignore one corrupt artifact; other artifacts remain usable */ }
        }
    }

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
                var payload = JsonSerializer.Serialize(new { modelVersion = dataset.ModelVersion ?? "sface", samples = dataset.Samples ?? [], negativeSamples = dataset.NegativeSamples ?? [] });
                var timeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("PICER_GPU_WORKER_TIMEOUT_SECONDS"), out var configured)
                    ? Math.Clamp(configured, 30, 86400) : 1800;
                var output = await _gpuWorker.ExecuteAsync(payload, TimeSpan.FromSeconds(timeoutSeconds));
                using var parsed = JsonDocument.Parse(output);
                if (parsed.RootElement.TryGetProperty("error", out var workerError)) throw new InvalidOperationException(workerError.GetString());
                var gpuArtifact = JsonSerializer.Deserialize<TrainingArtifact>(output, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("GPU worker returned invalid artifact");
                gpuArtifact = _signer.Sign(gpuArtifact);
                StoreArtifact(gpuArtifact);
                jobs[jobId] = jobs[jobId] with { Status = "completed", Stage = "gpu-worker-completed", Progress = 100, ArtifactId = gpuArtifact.ArtifactId };
                _archive.Append(new TrainingAttemptRecord("", "", jobId, gpuArtifact.ArtifactId, dataset.Attempt,
                    dataset.ModelVersion ?? "sface", dataset.Samples?.Length ?? 0,
                    dataset.Samples?.Select(sample => sample.PersonId).Distinct().Count() ?? 0,
                    "completed", gpuArtifact.Metrics.ValidationAccuracy, gpuArtifact.Metrics.FalseAcceptRate,
                    gpuArtifact.Metrics.FalseRejectRate, dataset.Attempt > 1 ? "glm-4.6v-flash" : null,
                    dataset.Attempt > 1 ? "face-training-advisor.v1" : null, dataset.TrainingHints, DateTime.UtcNow,
                    dataset.RecipeFingerprint, dataset.RecipeReason, dataset.SelectedSampleCount, dataset.ExcludedSampleCount,
                    false, dataset.Scenario));
                _rankings.Append(new ModelCandidateRound(dataset.ModelVersion ?? "sface", "prototype-calibration", dataset.Attempt,
                    gpuArtifact.Metrics.ValidationAccuracy, gpuArtifact.Metrics.FalseAcceptRate, gpuArtifact.Metrics.FalseRejectRate,
                    CalibrationGate.Passes(gpuArtifact.Metrics),
                    gpuArtifact.ArtifactId, DateTime.UtcNow, dataset.Scenario,
                    dataset.EvaluationId ?? $"{dataset.SnapshotId}:attempt:{dataset.Attempt}"));
                return;
            }
            if (dataset.Samples?.Any(s => s.Embedding is null || s.Embedding.Length == 0) == true)
            {
                jobs[jobId] = jobs[jobId] with { Status = "failed", Error = "当前 CPU 训练路径需要 embedding；请使用 SFace/本地特征或启用 InsightFace GPU worker" };
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

            var trainSamples = samples.Where(s => s.Split == "train" && s.Label != "negative").ToList();
            var validationSamples = samples.Where(s => s.Split == "validation").ToList();
            if (trainSamples.Count == 0 || !validationSamples.Any(s => s.Label != "negative"))
                throw new InvalidOperationException("需要独立的训练集和正样本验证集");
            jobs[jobId] = jobs[jobId] with { Stage = "clustering", Progress = 25 };
            await Task.Delay(200);

            var prototypes = new List<PersonPrototype>();
            foreach (var personGroup in trainSamples.GroupBy(s => s.PersonId))
            {
                var embeddings = personGroup.Select(s => s.Embedding!).ToList();
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

            var metrics = TrainingMetricsEvaluator.Evaluate(prototypes, samples, dataset.NegativeSamples ?? []);

            var artifactId = "artifact-" + Guid.NewGuid().ToString("N")[..12];
            var artifact = _signer.Sign(new TrainingArtifact(artifactId, "prototype-calibration", dataset.ModelVersion ?? "sface", DateTime.UtcNow, prototypes, metrics));
            StoreArtifact(artifact);

            jobs[jobId] = jobs[jobId] with { Status = "completed", Stage = "completed", Progress = 100, ArtifactId = artifactId };
            _archive.Append(new TrainingAttemptRecord("", "", jobId, artifactId, dataset.Attempt,
                dataset.ModelVersion ?? "sface", samples.Length, prototypes.Count, "completed",
                metrics.ValidationAccuracy, metrics.FalseAcceptRate, metrics.FalseRejectRate,
                dataset.Attempt > 1 ? "glm-4.6v-flash" : null,
                dataset.Attempt > 1 ? "face-training-advisor.v1" : null, dataset.TrainingHints, DateTime.UtcNow,
                dataset.RecipeFingerprint, dataset.RecipeReason, dataset.SelectedSampleCount, dataset.ExcludedSampleCount,
                false, dataset.Scenario));
            _rankings.Append(new ModelCandidateRound(dataset.ModelVersion ?? "sface", "prototype-calibration", dataset.Attempt,
                metrics.ValidationAccuracy, metrics.FalseAcceptRate, metrics.FalseRejectRate,
                CalibrationGate.Passes(metrics),
                artifactId, DateTime.UtcNow, dataset.Scenario,
                dataset.EvaluationId ?? $"{dataset.SnapshotId}:attempt:{dataset.Attempt}"));
        }
        catch (Exception exception)
        {
            jobs[jobId] = jobs[jobId] with { Status = "failed", Error = exception.Message };
            _archive.Append(new TrainingAttemptRecord("", "", jobId, null, dataset.Attempt,
                dataset.ModelVersion ?? "sface", dataset.Samples?.Length ?? 0,
                dataset.Samples?.Select(sample => sample.PersonId).Distinct().Count() ?? 0,
                "failed", null, null, null, dataset.Attempt > 1 ? "glm-4.6v-flash" : null,
                dataset.Attempt > 1 ? "face-training-advisor.v1" : null, dataset.TrainingHints, DateTime.UtcNow,
                dataset.RecipeFingerprint, dataset.RecipeReason, dataset.SelectedSampleCount, dataset.ExcludedSampleCount,
                false, dataset.Scenario));
        }
    }

    private void StoreArtifact(TrainingArtifact artifact)
    {
        _artifacts[artifact.ArtifactId] = artifact;
        var path = Path.Combine(_artifactRoot, artifact.ArtifactId + ".json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(artifact, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.Move(temp, path, true);
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
