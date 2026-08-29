using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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
        var output = await RunAsync(_ffprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:format=duration", "-of", "json", videoPath], cancellationToken);
        using var json = JsonDocument.Parse(output.StdOut);
        var stream = json.RootElement.GetProperty("streams")[0];
        var format = json.RootElement.GetProperty("format");
        return new VideoProbeResult(double.Parse(format.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture),
            stream.GetProperty("width").GetInt32(), stream.GetProperty("height").GetInt32());
    }

    public async Task<IReadOnlyList<VideoKeyframe>> ExtractAsync(VideoRecord video, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(cacheRoot, video.Id.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var pattern = Path.Combine(directory, "scene-%05d.jpg");
        var run = await RunAsync(ffmpegPath, ["-hide_banner", "-loglevel", "info", "-i", video.Path,
            "-vf", "select=gt(scene\\,0.35),showinfo,scale=1280:-2", "-vsync", "vfr", "-q:v", "3", pattern], cancellationToken, throwOnError: false);
        var files = Directory.EnumerateFiles(directory, "scene-*.jpg").Order().Take(200).ToArray();
        if (files.Length == 0)
        {
            pattern = Path.Combine(directory, "interval-%05d.jpg");
            await RunAsync(ffmpegPath, ["-hide_banner", "-loglevel", "error", "-i", video.Path,
                "-vf", "fps=1/10,scale=1280:-2", "-frames:v", "200", "-q:v", "3", pattern], cancellationToken);
            files = Directory.EnumerateFiles(directory, "interval-*.jpg").Order().ToArray();
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
