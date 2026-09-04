using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Picer.Core.Data;
using Picer.Core.Hashing;

namespace Picer.Core.Video;

public sealed record VideoProbeResult(double DurationSeconds, int Width, int Height);
public sealed record VideoKeyframe(double TimestampSeconds, string Path, long DifferenceHash = 0);

public sealed class FfmpegKeyframeService(string ffmpegPath, string cacheRoot)
{
    private readonly string _ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");

    public async Task<VideoProbeResult> ProbeAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_ffprobePath))
        {
            var fallback = await RunAsync(ffmpegPath, ["-hide_banner", "-i", videoPath], cancellationToken, throwOnError: false);
            return ParseFfmpegProbeOutput(fallback.StdErr);
        }
        var output = await RunAsync(_ffprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:format=duration", "-of", "json", videoPath], cancellationToken);
        using var json = JsonDocument.Parse(output.StdOut);
        var stream = json.RootElement.GetProperty("streams")[0];
        var format = json.RootElement.GetProperty("format");
        return new VideoProbeResult(double.Parse(format.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture),
            stream.GetProperty("width").GetInt32(), stream.GetProperty("height").GetInt32());
    }

    public static VideoProbeResult ParseFfmpegProbeOutput(string output)
    {
        var durationMatch = Regex.Match(output, @"Duration:\s*(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.CultureInvariant);
        var videoLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Contains("Video:", StringComparison.OrdinalIgnoreCase)) ?? "";
        var resolutionMatch = Regex.Match(videoLine, @"(?<!\d)(\d{2,5})x(\d{2,5})(?!\d)", RegexOptions.CultureInvariant);
        if (!durationMatch.Success || !resolutionMatch.Success)
            throw new InvalidOperationException("FFmpeg 未返回可识别的视频时长或分辨率信息。");
        var duration = TimeSpan.FromHours(int.Parse(durationMatch.Groups[1].Value, CultureInfo.InvariantCulture)) +
                       TimeSpan.FromMinutes(int.Parse(durationMatch.Groups[2].Value, CultureInfo.InvariantCulture)) +
                       TimeSpan.FromSeconds(double.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture));
        return new VideoProbeResult(duration.TotalSeconds,
            int.Parse(resolutionMatch.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(resolutionMatch.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    public async Task<IReadOnlyList<VideoKeyframe>> ExtractAsync(VideoRecord video, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(cacheRoot, video.Id.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        foreach (var stale in Directory.EnumerateFiles(directory, "scene-*.jpg").Concat(Directory.EnumerateFiles(directory, "interval-*.jpg")))
            File.Delete(stale);
        var pattern = Path.Combine(directory, "scene-%05d.jpg");
        var run = await RunAsync(ffmpegPath, ["-hide_banner", "-loglevel", "info", "-i", video.Path,
            "-vf", "select=gt(scene\\,0.35),showinfo,scale=1280:-2,format=yuvj420p", "-fps_mode", "vfr",
            "-c:v", "mjpeg", "-strict", "unofficial", "-q:v", "3", pattern], cancellationToken, throwOnError: false);
        var files = Directory.EnumerateFiles(directory, "scene-*.jpg").Order().Take(200).ToArray();
        if (files.Length == 0)
        {
            pattern = Path.Combine(directory, "interval-%05d.jpg");
            var fallback = await RunAsync(ffmpegPath, ["-hide_banner", "-loglevel", "error", "-i", video.Path,
                "-vf", "select=isnan(prev_selected_t)+gte(t-prev_selected_t\\,10),scale=1280:-2,format=yuvj420p",
                "-fps_mode", "vfr", "-frames:v", "200", "-c:v", "mjpeg", "-strict", "unofficial", "-q:v", "3", pattern],
                cancellationToken, throwOnError: false);
            files = Directory.EnumerateFiles(directory, "interval-*.jpg").Order().ToArray();
            if (files.Length == 0)
                throw new InvalidOperationException("FFmpeg 未生成关键帧。场景检测：" + CompactDiagnostic(run.StdErr) +
                                                    "；定时抽帧：" + CompactDiagnostic(fallback.StdErr));
            return Deduplicate(files.Select((path, index) => new VideoKeyframe(index * 10d, path)));
        }
        var timestamps = run.StdErr.Split('\n').Select(line =>
        {
            var marker = line.IndexOf("pts_time:", StringComparison.Ordinal);
            if (marker < 0) return (double?)null;
            var value = line[(marker + 9)..].Split(' ', '\r')[0];
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var time) ? time : null;
        }).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return Deduplicate(files.Select((path, index) => new VideoKeyframe(index < timestamps.Length ? timestamps[index] : index, path)));
    }

    public async Task<string> GetOrCreatePosterAsync(VideoRecord video, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(cacheRoot, video.Id.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var poster = Path.Combine(directory, "poster.jpg");
        if (File.Exists(poster) && File.GetLastWriteTimeUtc(poster) >= video.ModifiedUtc) return poster;
        var temporary = poster + ".tmp.jpg";
        if (File.Exists(temporary)) File.Delete(temporary);
        await RunAsync(ffmpegPath, ["-hide_banner", "-loglevel", "error", "-ss", "0.2", "-i", video.Path,
            "-frames:v", "1", "-vf", "scale=640:-2,format=yuvj420p", "-c:v", "mjpeg", "-q:v", "4", "-y", temporary], cancellationToken);
        File.Move(temporary, poster, true);
        return poster;
    }

    private static string CompactDiagnostic(string value)
    {
        var lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(4)).Trim() is { Length: > 0 } message ? message : "没有错误输出";
    }

    private static IReadOnlyList<VideoKeyframe> Deduplicate(IEnumerable<VideoKeyframe> frames)
    {
        var kept=new List<VideoKeyframe>();
        foreach(var frame in frames){using var stream=File.OpenRead(frame.Path);var hash=PerceptualHasher.ComputeDifferenceHash(stream);
            if(kept.Any(existing=>PerceptualHasher.HammingDistance(existing.DifferenceHash,hash)<=6))continue;
            kept.Add(frame with{DifferenceHash=hash});}
        return kept;
    }

    private static async Task<(string StdOut, string StdErr)> RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, bool throwOnError = true)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动FFmpeg进程。");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var result = (await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        if (throwOnError && process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg失败（{process.ExitCode}）：{result.Item2}");
        return result;
    }
}
