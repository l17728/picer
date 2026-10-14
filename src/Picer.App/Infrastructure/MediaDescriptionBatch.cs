using System.IO;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.App.Infrastructure;

internal sealed class DescriptionBudgetExceededException() : InvalidOperationException("已达到描述费用预算，已完成结果保留。请调整预算或稍后继续。");
internal sealed class DescriptionServiceUnavailableException() : InvalidOperationException("服务连续不可用，任务已暂停，未处理项保持待描述。请检查来源设备、索引服务和模型服务后重新开始；已完成结果会自动跳过。");
internal sealed record MediaDescriptionFailure(bool IsVideo, long Id, string Path, string Error,
    AnalysisStage Stage = AnalysisStage.Model, bool Retryable = true)
{
    public override string ToString() => $"{(IsVideo ? "视频" : "照片")}：{Path} — {Stage switch { AnalysisStage.Scan => "扫描", AnalysisStage.Preprocess => "预处理", AnalysisStage.Save => "保存", _ => "模型分析" }}：{Error}（{(Retryable ? "可重试" : "需调整输入或配置")}）";
}

/// <summary>Incremental coordinator. Completed descriptions are the durable resume checkpoint.</summary>
internal sealed class MediaDescriptionBatch(
    Func<ImageRecord, CancellationToken, Task<bool>> describePhoto,
    Func<VideoRecord, CancellationToken, Task<bool>> describeVideo)
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
        int photoConcurrency = 1, int videoConcurrency = 1, IProgress<MediaDescriptionFailure>? failures = null, int initialSkipped = 0)
    {
        var completed = 0;
        var failed = 0;
        var count = 0;
        var reportGate = new object();
        var serviceFailures = 0;
        var discovered = 0;
        var skipped = initialSkipped;
        var runId = Guid.NewGuid().ToString("N");
        void Report() { lock (reportGate) progress.Report($"已发现待描述 {discovered} 项；已处理 {count}/{discovered}：成功或复用 {completed}，失败 {failed}；原有描述跳过 {skipped}（持续发现中）"); }
        async Task RunOne(Func<Task<bool>> work, string kind, long id, string path, CancellationToken workerToken)
        {
            token.ThrowIfCancellationRequested();
            var attemptId = Guid.NewGuid().ToString("N");
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (await work()) { Interlocked.Increment(ref completed); Interlocked.Exchange(ref serviceFailures, 0); }
                else { Interlocked.Increment(ref failed); failures?.Report(new(kind == "video", id, path, "analysis_not_completed")); Core.Logging.AppLogger.Warn($"Directory description item failed; runId={runId}; attemptId={attemptId}; kind={kind}; mediaId={id}; stage=Unknown; errorType=NoResult; innerTypes=none; code=analysis_not_completed; retryable=true; elapsedMs={ElapsedMilliseconds(started)}"); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || workerToken.IsCancellationRequested) { throw; }
            catch (DescriptionBudgetExceededException) { throw; }
            catch (AnalysisBudgetExceededException) { throw; }
            catch (Exception exception)
            {
                Interlocked.Increment(ref failed);
                var detail = exception as AnalysisStageException ?? AnalysisStageException.Wrap(AnalysisStage.Model, exception);
                failures?.Report(new(kind == "video", id, path, detail.ErrorCode, detail.Stage, detail.Retryable));
                Core.Logging.AppLogger.Warn($"Directory description item failed; runId={runId}; attemptId={attemptId}; kind={kind}; mediaId={id}; stage={detail.Stage}; errorType={SafeType(exception)}; innerTypes={SafeInnerTypes(exception)}; code={SafeCode(detail.ErrorCode)}; retryable={detail.Retryable}; elapsedMs={ElapsedMilliseconds(started)}");
                if (detail.ErrorCode is "HTTP_401" or "HTTP_403" or "model_not_configured" ||
                    IsServiceFailure(detail.ErrorCode) && Interlocked.Increment(ref serviceFailures) >= 6)
                    throw new DescriptionServiceUnavailableException();
                if (!IsServiceFailure(detail.ErrorCode)) Interlocked.Exchange(ref serviceFailures, 0);
            }
            Interlocked.Increment(ref count); Report();
        }
        using var photos = new SemaphoreSlim(Math.Clamp(photoConcurrency, 1, 4));
        using var videos = new SemaphoreSlim(Math.Clamp(videoConcurrency, 1, 4));
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Math.Max(photoConcurrency, videoConcurrency), 1, 4), CancellationToken = token },
            async (item, workerToken) =>
            {
                if (item.Described) { Interlocked.Increment(ref skipped); if (skipped % 32 == 0) Report(); return; }
                Interlocked.Increment(ref discovered); Report();
                var gate = item.IsVideo ? videos : photos;
                await gate.WaitAsync(workerToken);
                try
                {
                    await RunOne(() => item.IsVideo ? describeVideo(item.Video!, workerToken) : describePhoto(item.Photo!, workerToken),
                        item.IsVideo ? "video" : "photo", item.Id, item.Path, workerToken);
                }
                finally { gate.Release(); }
            });
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

    internal sealed class PhotoInputAnalyzer(ICloudVisionAnalyzer inner, string cacheRoot, IMediaAssetGateway? assets = null) : ICloudVisionAnalyzer
    {
        public string Provider => inner.Provider;
        public string ModelVersion => inner.ModelVersion;
        public string PromptVersion => inner.PromptVersion;
        public async Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken token)
        {
            Directory.CreateDirectory(cacheRoot);
            var path = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N") + Path.GetExtension(request.Image.Path));
            var stage = AnalysisStage.Preprocess;
            try
            {
                var prepared = assets is not null ? await assets.OpenPhotoAnalysisInputAsync(request.Image, token) : null;
                var sourcePrepared = prepared is not null;
                await using (var stream = prepared ?? (assets is not null
                    ? await assets.OpenOriginalImageAsync(request.Image.Id, cancellationToken: token) ?? throw new IOException("原图不可用")
                    : new FileStream(request.Image.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan)))
                {
                    if (assets is null && File.GetLastWriteTimeUtc(request.Image.Path) != request.Image.ModifiedUtc)
                        throw new InvalidDataException("原图已变化，请先更新索引。");
                    await using var output = File.Create(path);
                    await stream.CopyToAsync(output, token);
                }
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
                return await inner.AnalyzeAsync(request with { Image = local }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { throw AnalysisStageException.Wrap(stage, exception); }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
