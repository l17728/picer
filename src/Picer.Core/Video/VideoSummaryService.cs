using System.Text;
using Picer.Core.Models;
using Picer.Core.Vision;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Picer.Core.Video;

public sealed class VideoSummaryService(VideoRepository repository, string cacheDirectory)
{
    public async Task<CloudVisionResult> AnalyzeAsync(VideoRecord video, ICloudVisionAnalyzer analyzer,
        CancellationToken cancellationToken = default)
    {
        var frames = repository.GetKeyframes(video.Id).Take(12).ToArray();
        if (frames.Length == 0) throw new InvalidOperationException("请先生成视频关键帧。");
        Directory.CreateDirectory(cacheDirectory);
        var montagePath = Path.Combine(cacheDirectory, $"video-{video.Id}-{video.FileSize}-{video.ModifiedUtc.Ticks}.jpg");
        if (!File.Exists(montagePath)) await CreateMontageAsync(frames, montagePath, cancellationToken).ConfigureAwait(false);

        var transcript = ExtractTranscript(repository.GetTranscript(video.Id));
        var context = new StringBuilder("这是按时间顺序排列的视频关键帧拼图，请生成整段视频的融合摘要，而不是逐图描述。");
        if (!string.IsNullOrWhiteSpace(transcript)) context.Append("\n本地语音转写：").Append(transcript[..Math.Min(6000, transcript.Length)]);
        if (!string.IsNullOrWhiteSpace(video.Gps)) context.Append("\n视频位置上下文：").Append(video.Gps);
        context.Append("\n不得根据人脸猜测姓名或亲属关系。");
        var info = new FileInfo(montagePath);
        var request = new CloudVisionRequest(new ImageRecord
        {
            Path = montagePath, FileSize = info.Length, ModifiedUtc = info.LastWriteTimeUtc,
            Width = 1280, Height = 720
        }, null, [], context.ToString());
        var result = await analyzer.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
        repository.SaveSummary(video.Id, analyzer, video, result);
        return result;
    }

    private static async Task CreateMontageAsync(IReadOnlyList<VideoKeyframe> frames, string output,
        CancellationToken cancellationToken)
    {
        const int cellWidth = 320, cellHeight = 180, columns = 3;
        var rows = (int)Math.Ceiling(frames.Count / (double)columns);
        using var canvas = new Image<Rgb24>(cellWidth * columns, cellHeight * rows, Color.Black);
        for (var i = 0; i < frames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var image = await Image.LoadAsync<Rgb24>(frames[i].Path, cancellationToken).ConfigureAwait(false);
            image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(cellWidth, cellHeight), Mode = ResizeMode.Pad, PadColor = Color.Black }));
            canvas.Mutate(x => x.DrawImage(image, new Point(i % columns * cellWidth, i / columns * cellHeight), 1f));
        }
        canvas.Metadata.ExifProfile = null; canvas.Metadata.IccProfile = null; canvas.Metadata.XmpProfile = null;
        await canvas.SaveAsJpegAsync(output, new JpegEncoder { Quality = 82 }, cancellationToken).ConfigureAwait(false);
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
