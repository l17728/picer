using System.IO;
using System.Threading.Channels;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.App.Infrastructure;

internal sealed class DescriptionBudgetExceededException() : InvalidOperationException("已达到描述费用预算，已完成结果保留。请调整预算或稍后继续。");
internal sealed class DescriptionServiceUnavailableException() : InvalidOperationException(
    "服务连续不可用，任务已暂停。当前显示的“已发现”只是暂停前的流式进度，不是目录总量；未发现和未处理项目仍保持待描述。请检查来源设备、索引服务和模型服务后重新开始，已有结果会自动跳过。");
internal sealed record MediaDescriptionFailure(bool IsVideo, long Id, string Path, string Error,
    AnalysisStage Stage = AnalysisStage.Model, bool Retryable = true)
{
    public override string ToString() => $"{(IsVideo ? "视频" : "照片")}：{Path} — {Stage switch { AnalysisStage.Scan => "扫描", AnalysisStage.Preprocess => "预处理", AnalysisStage.Save => "保存", _ => "模型分析" }}：{Error}（{(Retryable ? "可重试" : "需调整输入或配置")}）";
}

/// <summary>Incremental coordinator. Completed descriptions are the durable resume checkpoint.</summary>
internal sealed class MediaDescriptionBatch(
    Func<ImageRecord, CancellationToken, Task<bool>> describePhoto,
    Func<VideoRecord, CancellationToken, Task<bool>> describeVideo,
    Func<ImageRecord, CancellationToken, Task<IDisposable?>>? prefetchPhoto = null)
{
    public Task RunAsync(DirectoryMediaDescriptionPlan plan, IProgress<string> progress, CancellationToken token,
        int photoConcurrency = 1, int videoConcurrency = 1, IProgress<MediaDescriptionFailure>? failures = null)
        => RunStreamingAsync(Items(plan), progress, token, photoConcurrency, videoConcurrency, failures, plan.SkippedCount);

    private static async IAsyncEnumerable<DirectoryDescriptionItem> Items(DirectoryMediaDescriptionPlan plan)
    {
        await Task.CompletedTask;
        foreach (var photo in plan.Photos) yield return new(photo, null, false);
        foreach (var video in plan.Videos) yield return new(null, video, false);
    }

    public async Task RunStreamingAsync(IAsyncEnumerable<DirectoryDescriptionItem> items, IProgress<string> progress, CancellationToken token,
        int photoConcurrency = 1, int videoConcurrency = 1, IProgress<MediaDescriptionFailure>? failures = null, int initialSkipped = 0, string? runId = null)
    {
        var completed = 0;
        var failed = 0;
        var count = 0;
        var reportGate = new object();
        var discovered = 0;
        var skipped = initialSkipped;
        runId ??= Guid.NewGuid().ToString("N");
        var active = 0;
        var runStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        using var monitorStop = new CancellationTokenSource();
        using var cancellationObserved = token.Register(() => Core.Logging.AnalysisDiagnosticLog.Info(
            $"Directory description cancel_observed; runId={runId}; active={Volatile.Read(ref active)}; elapsedMs={ElapsedMilliseconds(runStarted)}"));
        var monitor = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                while (await timer.WaitForNextTickAsync(monitorStop.Token).ConfigureAwait(false))
                    if (token.IsCancellationRequested)
                        Core.Logging.AnalysisDiagnosticLog.Info($"Directory description cancellation waiting; runId={runId}; phase=worker_drain; active={Volatile.Read(ref active)}; elapsedMs={ElapsedMilliseconds(runStarted)}");
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
        });
        void Report() { lock (reportGate) progress.Report($"已发现待描述 {discovered} 项；已处理 {count}/{discovered}：成功或复用 {completed}，失败 {failed}；原有描述跳过 {skipped}（持续发现中）"); }
        async Task RunOne(Func<Task<bool>> work, string kind, long id, string path, CancellationToken workerToken)
        {
            token.ThrowIfCancellationRequested();
            var attemptId = Guid.NewGuid().ToString("N");
            using var trace = Core.Logging.AnalysisTrace.Begin(attemptId);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref active);
            try
            {
                if (await work()) { Interlocked.Increment(ref completed);
                    Core.Logging.AnalysisDiagnosticLog.Info($"Directory description item completed or reused; runId={runId}; attemptId={attemptId}; kind={kind}; mediaId={id}; elapsedMs={ElapsedMilliseconds(started)}"); }
                else { Interlocked.Increment(ref failed); failures?.Report(new(kind == "video", id, path, "analysis_not_completed")); Core.Logging.AnalysisDiagnosticLog.Warn($"Directory description item failed; runId={runId}; attemptId={attemptId}; kind={kind}; mediaId={id}; stage=Unknown; errorType=NoResult; innerTypes=none; code=analysis_not_completed; retryable=true; elapsedMs={ElapsedMilliseconds(started)}"); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || workerToken.IsCancellationRequested)
            {
                Core.Logging.AnalysisDiagnosticLog.Info($"Directory description item cancelled; runId={runId}; attemptId={attemptId}; kind={kind}; mediaId={id}; elapsedMs={ElapsedMilliseconds(started)}");
                throw;
            }
            catch (DescriptionBudgetExceededException) { throw; }
            catch (AnalysisBudgetExceededException) { throw; }
            catch (Exception exception)
            {
                Interlocked.Increment(ref failed);
                var detail = exception as AnalysisStageException ?? AnalysisStageException.Wrap(AnalysisStage.Model, exception);
                failures?.Report(new(kind == "video", id, path, detail.ErrorCode, detail.Stage, detail.Retryable));
                Core.Logging.AnalysisDiagnosticLog.Warn($"Directory description item failed; runId={runId}; attemptId={attemptId}; kind={kind}; mediaId={id}; stage={detail.Stage}; errorType={SafeType(exception)}; innerTypes={SafeInnerTypes(exception)}; code={SafeCode(detail.ErrorCode)}; retryable={detail.Retryable}; elapsedMs={ElapsedMilliseconds(started)}");
                if (detail.ErrorCode is "HTTP_401" or "HTTP_403" or "model_not_configured")
                    throw new DescriptionServiceUnavailableException();
            }
            finally { Interlocked.Decrement(ref active); }
            Interlocked.Increment(ref count); Report();
        }
        using var photos = new SemaphoreSlim(Math.Clamp(photoConcurrency, 1, 4));
        using var videos = new SemaphoreSlim(Math.Clamp(videoConcurrency, 1, 4));
        using var workersCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var workerToken = workersCancellation.Token;
        var discoveredItems = Channel.CreateBounded<DirectoryDescriptionItem>(new BoundedChannelOptions(8192)
        { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var queue = Channel.CreateBounded<(DirectoryDescriptionItem Item, IDisposable? Prepared)>(new BoundedChannelOptions(32)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        var workerCount = Math.Clamp(Math.Max(photoConcurrency, videoConcurrency), 1, 4);
        async Task WorkerAsync()
        {
            try
            {
                await foreach (var queued in queue.Reader.ReadAllAsync(workerToken).ConfigureAwait(false))
                {
                    using var prepared = queued.Prepared;
                    var item = queued.Item;
                    var gate = item.IsVideo ? videos : photos;
                    await gate.WaitAsync(workerToken).ConfigureAwait(false);
                    try
                    {
                        await RunOne(() => item.IsVideo ? describeVideo(item.Video!, workerToken) : describePhoto(item.Photo!, workerToken),
                            item.IsVideo ? "video" : "photo", item.Id, item.Path, workerToken).ConfigureAwait(false);
                    }
                    finally { gate.Release(); }
                }
            }
            catch { await workersCancellation.CancelAsync().ConfigureAwait(false); throw; }
        }
        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(WorkerAsync, workerToken)).ToArray();
        var preparation = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in discoveredItems.Reader.ReadAllAsync(workerToken).ConfigureAwait(false))
                {
                    IDisposable? prepared = null;
                    try
                    {
                        if (item.Photo is { } image && prefetchPhoto is not null)
                            prepared = await prefetchPhoto(image, workerToken).ConfigureAwait(false);
                        await queue.Writer.WriteAsync((item, prepared), workerToken).ConfigureAwait(false);
                        prepared = null;
                    }
                    finally { prepared?.Dispose(); }
                }
            }
            catch { await workersCancellation.CancelAsync().ConfigureAwait(false); throw; }
            finally { queue.Writer.TryComplete(); }
        }, workerToken);
        try
        {
            await foreach (var item in items.WithCancellation(workerToken).ConfigureAwait(false))
            {
                if (item.Described)
                {
                    Interlocked.Increment(ref skipped);
                    if (skipped % 32 == 0) Report();
                    continue;
                }
                var found = Interlocked.Increment(ref discovered);
                if (found == 1 || found % 16 == 0) Report();
                await discoveredItems.Writer.WriteAsync(item, workerToken).ConfigureAwait(false);
            }
            discoveredItems.Writer.TryComplete();
            await preparation.ConfigureAwait(false);
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch
        {
            queue.Writer.TryComplete();
            discoveredItems.Writer.TryComplete();
            await workersCancellation.CancelAsync().ConfigureAwait(false);
            Exception? workerFailure = null;
            try { await Task.WhenAll(workers.Append(preparation)).ConfigureAwait(false); }
            catch (Exception exception) when (!token.IsCancellationRequested) { workerFailure = exception; }
            if (workerFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(workerFailure).Throw();
            throw;
        }
        finally
        {
            while (queue.Reader.TryRead(out var pending)) pending.Prepared?.Dispose();
            await monitorStop.CancelAsync().ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
            Core.Logging.AnalysisDiagnosticLog.Info($"Directory description worker_drained; runId={runId}; active={Volatile.Read(ref active)}; elapsedMs={ElapsedMilliseconds(runStarted)}; cancelled={token.IsCancellationRequested}");
        }
        progress.Report($"发现完成；已处理 {count}/{discovered}：成功或复用 {completed}，失败 {failed}；原有描述跳过 {skipped}");
    }

    private static bool IsServiceFailure(string code) => code is "HTTP_408" or "HTTP_429" or "HTTP_500" or "HTTP_502" or "HTTP_503" or "HTTP_504"
        or "HttpRequestException" or "request_timeout" or "media_channel_busy" || code.StartsWith("socket_", StringComparison.Ordinal);
    private static long ElapsedMilliseconds(long started) => (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    private static string SafeType(Exception exception) => exception.GetType().Name is { Length: <= 64 } name && name.All(char.IsAsciiLetterOrDigit) ? name : "Exception";
    private static string SafeInnerTypes(Exception exception)
    {
        var types = new List<string>();
        for (var inner = exception.InnerException; inner is not null && types.Count < 4; inner = inner.InnerException)
            types.Add(SafeType(inner));
        return types.Count == 0 ? "none" : string.Join(',', types);
    }
    private static string SafeCode(string code) => code.Length <= 64 && code.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') ? code : "analysis_failed";

    internal sealed class PhotoInputAnalyzer(ICloudVisionAnalyzer inner, string cacheRoot, IMediaAssetGateway? assets = null,
        PreparedPhotoInputs? inputs = null) : ICloudVisionAnalyzer
    {
        public string Provider => inner.Provider;
        public string ModelVersion => inner.ModelVersion;
        public string PromptVersion => inner.PromptVersion;
        public async Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken token)
        {
            var path = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N") + Path.GetExtension(request.Image.Path));
            var stage = AnalysisStage.Preprocess;
            var traceId = Core.Logging.AnalysisTrace.Current ?? Guid.NewGuid().ToString("N");
            var step = "prepare_cache";
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var stepStarted = started;
            void NextStep(string next)
            {
                Core.Logging.AnalysisDiagnosticLog.Info($"Directory photo input step completed; attemptId={traceId}; imageId={request.Image.Id}; step={step}; elapsedMs={ElapsedMilliseconds(stepStarted)}; totalMs={ElapsedMilliseconds(started)}");
                step = next;
                stepStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                Core.Logging.AnalysisDiagnosticLog.Info($"Directory photo input step started; attemptId={traceId}; imageId={request.Image.Id}; step={step}");
            }
            try
            {
                Directory.CreateDirectory(cacheRoot);
                Stream? prepared = null;
                if (assets is not null)
                {
                    NextStep("prepared_input");
                    try
                    {
                        var prefetched = false;
                        if (inputs is not null) prepared = inputs.Open(request.Image, out prefetched);
                        if (!prefetched) prepared = await assets.OpenPhotoAnalysisInputAsync(request.Image, token);
                    }
                    catch (InvalidDataException exception)
                    {
                        // A remote prepared derivative can be rejected when a
                        // mirror fingerprint is stale or a proxy strips its
                        // verification metadata. Fall back to the authorized
                        // original stream; length/hash checks below still gate
                        // model access and the analyzer still strips metadata.
                        Core.Logging.AnalysisDiagnosticLog.Warn($"Prepared remote photo input rejected; attemptId={traceId}; imageId={request.Image.Id}; step={step}; reason={SafeCode(exception.Message)}; errorType={SafeType(exception)}; innerTypes={SafeInnerTypes(exception)}; hresult=0x{exception.HResult:X8}; elapsedMs={ElapsedMilliseconds(stepStarted)}; fallback=authorized-original");
                    }
                }
                var sourcePrepared = prepared is not null;
                if (prepared is null) NextStep("original_open");
                await using (var stream = prepared ?? (assets is not null
                    ? await assets.OpenOriginalImageAsync(request.Image.Id, cancellationToken: token) ?? throw new IOException("原图不可用")
                    : new FileStream(request.Image.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan)))
                {
                    NextStep("copy");
                    if (assets is null && File.GetLastWriteTimeUtc(request.Image.Path) != request.Image.ModifiedUtc)
                        throw new InvalidDataException("原图已变化，请先更新索引。");
                    await using var output = File.Create(path);
                    var maximumBytes = sourcePrepared ? 8L * 1024 * 1024 : request.Image.FileSize;
                    var buffer = new byte[65536]; long copied = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, token)) > 0)
                    {
                        copied += read;
                        if (copied > maximumBytes) throw new InvalidDataException("照片输入长度不匹配");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                }
                NextStep("verify");
                var inputSize = new FileInfo(path).Length;
                if (sourcePrepared ? inputSize is <= 0 or > 8 * 1024 * 1024 : inputSize != request.Image.FileSize)
                    throw new InvalidDataException("照片输入长度不匹配");
                if (!sourcePrepared && (request.Image.ContentSha256 ?? request.Image.Sha256) is { Length: 64 } expected)
                {
                    await using var input = File.OpenRead(path);
                    var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, token));
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("原图内容已变化，请先更新索引。");
                }
                var image = request.Image;
                var local = new ImageRecord { Id = image.Id, Path = path, FileSize = inputSize,
                    ModifiedUtc = image.ModifiedUtc, TakenUtc = image.TakenUtc, CreatedUtc = image.CreatedUtc,
                    Width = image.Width, Height = image.Height, Gps = image.Gps, Camera = image.Camera };
                stage = AnalysisStage.Model;
                NextStep("model");
                var result = await inner.AnalyzeAsync(request with { Image = local }, token);
                Core.Logging.AnalysisDiagnosticLog.Info($"Directory photo input step completed; attemptId={traceId}; imageId={request.Image.Id}; step={step}; elapsedMs={ElapsedMilliseconds(stepStarted)}; totalMs={ElapsedMilliseconds(started)}");
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Core.Logging.AnalysisDiagnosticLog.Info($"Directory photo input cancelled; attemptId={traceId}; imageId={request.Image.Id}; step={step}; elapsedMs={ElapsedMilliseconds(stepStarted)}; totalMs={ElapsedMilliseconds(started)}");
                throw;
            }
            catch (Exception exception)
            {
                var failure = AnalysisStageException.Wrap(stage, exception);
                Core.Logging.AnalysisDiagnosticLog.Warn($"Directory photo input failed; attemptId={traceId}; imageId={request.Image.Id}; step={step}; errorType={SafeType(exception)}; innerTypes={SafeInnerTypes(exception)}; hresult=0x{exception.HResult:X8}; code={SafeCode(failure.ErrorCode)}; retryable={failure.Retryable}; elapsedMs={ElapsedMilliseconds(stepStarted)}; totalMs={ElapsedMilliseconds(started)}");
                throw failure;
            }
            finally { DeleteTemporaryInput(path, request.Image.Id, traceId); }
        }

        private static string SafeCode(string value) => value.Length <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            ? value : "prepared_input_rejected";
    }

    internal static void DeleteTemporaryInput(string path, long imageId, string traceId)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            File.Delete(path);
            Core.Logging.AnalysisDiagnosticLog.Info($"Directory photo input step completed; attemptId={traceId}; imageId={imageId}; step=cleanup; elapsedMs={ElapsedMilliseconds(started)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Core.Logging.AnalysisDiagnosticLog.Warn($"Directory description temporary cleanup deferred; attemptId={traceId}; imageId={imageId}; step=cleanup; errorType={SafeType(exception)}; innerTypes={SafeInnerTypes(exception)}; hresult=0x{exception.HResult:X8}; elapsedMs={ElapsedMilliseconds(started)}");
        }
    }
}
