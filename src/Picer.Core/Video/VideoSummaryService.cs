using Picer.Core.Imaging;
using System.Text;
using Picer.Core.Models;
using Picer.Core.Vision;

namespace Picer.Core.Video;

public sealed class VideoSummaryService(VideoRepository repository, string cacheDirectory,
    Func<Action, CancellationToken, Task>? databaseWork = null)
{
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private static readonly object ContentGateLock = new();
    private static readonly Dictionary<(string Database, long Content), ContentGate> ContentGates = [];
    private sealed class ContentGate { public readonly SemaphoreSlim Semaphore = new(1, 1); public int Users; }

    private async Task DatabaseAsync(Action work, CancellationToken token)
    {
        if (databaseWork is not null) { await databaseWork(work, token).ConfigureAwait(false); return; }
        await _databaseGate.WaitAsync(token).ConfigureAwait(false);
        try { work(); }
        finally { _databaseGate.Release(); }
    }
    public async Task<CloudVisionResult> AnalyzeAsync(VideoRecord video, ICloudVisionAnalyzer analyzer,
        CancellationToken cancellationToken = default)
        => await AnalyzeAsync(video, analyzer, null, cancellationToken).ConfigureAwait(false);

    public async Task<CloudVisionResult> AnalyzeAsync(VideoRecord video, ICloudVisionAnalyzer analyzer,
        IReadOnlyList<VideoKeyframe>? framesOverride, CancellationToken cancellationToken = default, CloudVisionRunPolicy? runPolicy = null)
    {
        runPolicy ??= new CloudVisionRunPolicy(Enabled: true);
        if (runPolicy.ClampCount(1) == 0)
            throw new InvalidOperationException("视频描述未启用，或本次允许的描述数量为零。");
        using var operation = VideoOperationLifetime.Begin(video.Path, cancellationToken);
        cancellationToken = operation.Token;
        long contentId = 0;
        await DatabaseAsync(() => contentId = repository.GetContentId(video.Id), cancellationToken).ConfigureAwait(false);
        var identity = OperatingSystem.IsWindows() ? repository.DatabaseIdentity.ToUpperInvariant() : repository.DatabaseIdentity;
        var key = (identity, contentId);
        ContentGate gate;
        lock (ContentGateLock)
        {
            if (!ContentGates.TryGetValue(key, out gate!)) ContentGates[key] = gate = new();
            gate.Users++;
        }
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            CloudVisionResult? existing = null;
            await DatabaseAsync(() => existing = repository.GetCurrentSummaryResult(video, analyzer), cancellationToken).ConfigureAwait(false);
            if (existing is not null) return existing;
            return await AnalyzeCoreAsync(video, analyzer, framesOverride, cancellationToken, runPolicy).ConfigureAwait(false);
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            lock (ContentGateLock)
                if (--gate.Users == 0) { ContentGates.Remove(key); gate.Semaphore.Dispose(); }
        }
    }

    private async Task<CloudVisionResult> AnalyzeCoreAsync(VideoRecord video, ICloudVisionAnalyzer analyzer,
        IReadOnlyList<VideoKeyframe>? framesOverride, CancellationToken cancellationToken, CloudVisionRunPolicy runPolicy)
    {
        var stage = AnalysisStage.Preprocess;
        try
        {
            VideoKeyframe[] frames = [];
            string? transcriptJson = null;
            long currentContentId = 0;
            await DatabaseAsync(() =>
            {
                if (!repository.IsCurrentContent(video.Id, video)) throw new InvalidOperationException("video_content_changed");
                currentContentId = repository.GetContentId(video.Id);
                frames = (framesOverride ?? repository.GetKeyframes(video.Id)).Take(12).ToArray();
                transcriptJson = repository.GetTranscript(video.Id);
            }, cancellationToken).ConfigureAwait(false);
            if (frames.Length == 0) throw new InvalidOperationException("请先生成视频关键帧。");
            Directory.CreateDirectory(cacheDirectory);
            var contentKey = video.Sha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit)
                ? hash.ToLowerInvariant() : "content-" + currentContentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var montagePath = Path.Combine(cacheDirectory, $"video-{video.Id}-{contentKey}-{video.FileSize}-{video.ModifiedUtc.Ticks}.jpg");
            if (!File.Exists(montagePath)) await CreateMontageAsync(frames, montagePath, cancellationToken).ConfigureAwait(false);

            var transcript = ExtractTranscript(transcriptJson);
            var context = new StringBuilder("这是按时间顺序排列的视频关键帧拼图，请生成整段视频的融合摘要，而不是逐图描述。");
            if (!string.IsNullOrWhiteSpace(transcript)) context.Append("\n本地语音转写：").Append(transcript[..Math.Min(6000, transcript.Length)]);
            if (runPolicy.IncludePreciseGps && !string.IsNullOrWhiteSpace(video.Gps))
                context.Append("\n视频位置上下文：").Append(video.Gps);
            context.Append("\n不得根据人脸猜测姓名或亲属关系。");
            var info = new FileInfo(montagePath);
            var request = new CloudVisionRequest(new ImageRecord
            {
                Path = montagePath, FileSize = info.Length, ModifiedUtc = info.LastWriteTimeUtc,
                Width = 1280, Height = 720
            }, null, [], context.ToString());
            var requestId = Guid.NewGuid().ToString("N");
            var reserved = false;
            await DatabaseAsync(() => reserved = repository.Billing.TryReserve(requestId, runPolicy.EstimatedRequestCostYuan,
                runPolicy.DailyBudgetYuan, runPolicy.MonthlyBudgetYuan), cancellationToken).ConfigureAwait(false);
            if (!reserved) throw new AnalysisBudgetExceededException();
            try
            {
                stage = AnalysisStage.Model;
                var result = await analyzer.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
                // Preserve a completed model response even if cancellation arrives while saving.
                stage = AnalysisStage.Save;
                await DatabaseAsync(() => repository.SaveSummary(video.Id, analyzer, video, result, requestId), CancellationToken.None).ConfigureAwait(false);
                return result;
            }
            finally { await DatabaseAsync(() => repository.Billing.Release(requestId), CancellationToken.None).ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AnalysisBudgetExceededException) { throw; }
        catch (Exception exception) { throw AnalysisStageException.Wrap(stage, exception); }
    }

    private static async Task CreateMontageAsync(IReadOnlyList<VideoKeyframe> frames, string output,
        CancellationToken cancellationToken)
    {
        const int cellWidth = 320, cellHeight = 180, columns = 3;
        var rows = (int)Math.Ceiling(frames.Count / (double)columns);
        using var canvas = new RasterImage(cellWidth * columns, cellHeight * rows, new RgbPixel(0, 0, 0));
        for (var i = 0; i < frames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var image = await RasterImage.LoadAsync(frames[i].Path, cancellationToken).ConfigureAwait(false);
            image.ResizePadded(cellWidth, cellHeight);
            canvas.Draw(image, i % columns * cellWidth, i / columns * cellHeight);
        }
        canvas.StripMetadata();
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await canvas.SaveAsJpegAsync(temporary, 82, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ExtractTranscript(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var texts = new List<string>();
            Walk(document.RootElement, texts);
            return string.Join(' ', texts);
        }
        catch (System.Text.Json.JsonException) { return json; }
        static void Walk(System.Text.Json.JsonElement element, List<string> texts)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var property in element.EnumerateObject())
                    if (property.NameEquals("text") && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    { var value = property.Value.GetString(); if (!string.IsNullOrWhiteSpace(value)) texts.Add(value.Trim()); }
                    else Walk(property.Value, texts);
            else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) Walk(child, texts);
        }
    }
}
