using System.IO;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.App.Infrastructure;

internal sealed class DescriptionBudgetExceededException() : InvalidOperationException("已达到描述费用预算，已完成结果保留。请调整预算或稍后继续。");
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
    public async Task RunAsync(DirectoryMediaDescriptionPlan plan, IProgress<string> progress, CancellationToken token,
        int photoConcurrency = 1, int videoConcurrency = 1, IProgress<MediaDescriptionFailure>? failures = null)
    {
        var completed = 0;
        var failed = 0;
        var count = 0;
        var reportGate = new object();
        async Task RunOne(Func<Task<bool>> work, string kind, long id, string path, CancellationToken workerToken)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (await work()) Interlocked.Increment(ref completed);
                else { Interlocked.Increment(ref failed); failures?.Report(new(kind == "video", id, path, "分析未完成，可重试")); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || workerToken.IsCancellationRequested) { throw; }
            catch (DescriptionBudgetExceededException) { throw; }
            catch (AnalysisBudgetExceededException) { throw; }
            catch (Exception exception)
            {
                Interlocked.Increment(ref failed);
                var detail = exception as AnalysisStageException ?? AnalysisStageException.Wrap(AnalysisStage.Model, exception);
                failures?.Report(new(kind == "video", id, path, detail.ErrorCode, detail.Stage, detail.Retryable));
                Core.Logging.AppLogger.Warn($"Directory description item failed; kind={kind}; errorType={exception.GetType().Name}");
            }
            lock (reportGate) progress.Report($"已处理 {++count}/{plan.PendingCount}：成功或复用 {completed}，失败 {failed}；原有描述跳过 {plan.SkippedCount}");
        }
        if (photoConcurrency <= 1)
            foreach (var photo in plan.Photos) await RunOne(() => describePhoto(photo, token), "photo", photo.Id, photo.Path, token);
        else
            await Parallel.ForEachAsync(plan.Photos, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(photoConcurrency, 1, 4), CancellationToken = token },
                async (photo, workerToken) => await RunOne(() => describePhoto(photo, workerToken), "photo", photo.Id, photo.Path, workerToken));
        if (videoConcurrency <= 1)
            foreach (var video in plan.Videos) await RunOne(() => describeVideo(video, token), "video", video.Id, video.Path, token);
        else
            await Parallel.ForEachAsync(plan.Videos, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(videoConcurrency, 1, 4), CancellationToken = token },
                async (video, workerToken) => await RunOne(() => describeVideo(video, workerToken), "video", video.Id, video.Path, workerToken));
    }

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
