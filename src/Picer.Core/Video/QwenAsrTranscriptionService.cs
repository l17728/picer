using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Picer.Core.Video;

public sealed class QwenAsrTranscriptionService(
    string ffmpegPath,
    Uri endpoint,
    string model,
    string workRoot,
    HttpClient? client = null) : IVideoTranscriptionService, IDisposable
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    private readonly bool _ownsClient = client is null;
    public string ModelVersion => model;

    public async Task<string> TranscribeAsync(VideoRecord video, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(workRoot, video.Id.ToString());
        Directory.CreateDirectory(directory);
        var wave = Path.Combine(directory, "audio-16k.wav");
        if (!File.Exists(wave))
            await ExtractAudioAsync(video.Path, wave, cancellationToken).ConfigureAwait(false);

        await using var stream = File.OpenRead(wave);
        using var form = new MultipartFormDataContent();
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", Path.GetFileName(wave));
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("zh"), "language");
        // Keep VAD deliberately permissive; the service must still process the complete audio track.
        form.Add(new StringContent("loose"), "vad_mode");
        form.Add(new StringContent("true"), "full_audio_fallback");
        form.Add(new StringContent("true"), "timestamps");
        using var response = await _client.PostAsync(endpoint, form, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Qwen3-ASR 调用失败（{(int)response.StatusCode}）：{content}");
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("Qwen3-ASR 返回了空结果。");
        try { using var _ = JsonDocument.Parse(content); return content; }
        catch (JsonException) { return JsonSerializer.Serialize(new { text = content.Trim() }); }
    }

    private async Task ExtractAudioAsync(string input, string output, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", input,
                     "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", output })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动音频提取进程。");
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"提取视频音频失败（{process.ExitCode}）：{await error.ConfigureAwait(false)}");
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
