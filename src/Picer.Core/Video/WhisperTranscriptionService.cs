using System.Diagnostics;

namespace Picer.Core.Video;

public interface IVideoTranscriptionService
{
    string ModelVersion { get; }
    Task<string> TranscribeAsync(VideoRecord video, CancellationToken cancellationToken = default);
}

public sealed class WhisperTranscriptionService(string ffmpegPath, string whisperPath, string modelPath, string workRoot)
    : IVideoTranscriptionService
{
    public string ModelVersion => Path.GetFileNameWithoutExtension(modelPath);

    public async Task<string> TranscribeAsync(VideoRecord video, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(workRoot, video.Id.ToString()); Directory.CreateDirectory(directory);
        var wave = Path.Combine(directory, "audio-16k.wav"); var outputPrefix = Path.Combine(directory, "transcript");
        if (!File.Exists(wave)) await RunAsync(ffmpegPath, ["-hide_banner","-loglevel","error","-y","-i",video.Path,"-vn","-ac","1","-ar","16000","-c:a","pcm_s16le",wave], cancellationToken);
        await RunAsync(whisperPath, ["-m",modelPath,"-f",wave,"-l","auto","-sns","-nf","-oj","-of",outputPrefix,"-np"], cancellationToken);
        var jsonPath = outputPrefix + ".json";
        if (!File.Exists(jsonPath)) throw new InvalidOperationException("Whisper未生成转写结果。");
        return await File.ReadAllTextAsync(jsonPath, cancellationToken);
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true };
        foreach(var argument in arguments) start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new InvalidOperationException("无法启动本地转写进程。");
        var error=process.StandardError.ReadToEndAsync(cancellationToken); var output=process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken); await output;
        if(process.ExitCode!=0) throw new InvalidOperationException($"本地转写失败（{process.ExitCode}）：{await error}");
    }
}
