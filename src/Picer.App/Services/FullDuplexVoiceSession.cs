using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;
using Picer.Core.Logging;

namespace Picer.App.Services;

public sealed class FullDuplexVoiceSession : IDisposable
{
    private const int SampleRate = 16000;
    private readonly Uri _asrEndpoint;
    private readonly Uri _ttsEndpoint;
    private readonly string _asrModel;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly MemoryStream _utterance = new();
    private WaveInEvent? _input;
    private WaveOutEvent? _output;
    private CancellationTokenSource? _recognition;
    private DateTime _lastSpeechUtc;
    private int _speechFrames;
    private bool _speaking;
    private bool _processing;
    private DateTime _lastLevelLogUtc;
    private int _playbackSpeechFrames;
    private DateTime _playbackEndedUtc = DateTime.MinValue;

    public event Action<string>? StateChanged;
    /// Raised for UI diagnostics only; contains an RMS level, never audio data.
    public event Action<double>? AudioLevelChanged;
    /// Raised when speech is detected while TTS is playing. The host must cancel
    /// the in-flight agent turn as well as stopping audio playback.
    public event Action? SpeechDetectedDuringPlayback;
    public event Func<string, Task>? UtteranceRecognized;
    public bool IsListening => _input is not null;

    public FullDuplexVoiceSession(Uri asrEndpoint, string asrModel, Uri ttsEndpoint)
    {
        _asrEndpoint = asrEndpoint;
        _asrModel = asrModel;
        _ttsEndpoint = ttsEndpoint;
    }

    public void Start()
    {
        if (_input is not null) return;
        _input = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 40 };
        _input.DataAvailable += OnAudio;
        _input.RecordingStopped += (_, args) => { if (args.Exception is not null) StateChanged?.Invoke($"麦克风异常：{args.Exception.Message}"); };
        _input.StartRecording();
        AppLogger.Info($"Voice listen started; endpoint={_asrEndpoint.Host}:{_asrEndpoint.Port}; model={_asrModel}");
        StateChanged?.Invoke("正在聆听（说话可打断播报）");
    }

    public void Stop()
    {
        _input?.StopRecording();
        _input?.Dispose();
        _input = null;
        CancelPlayback();
        _recognition?.Cancel();
        lock (_utterance) { _utterance.SetLength(0); }
        StateChanged?.Invoke("语音已停止");
    }

    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        if (DateTime.UtcNow - _playbackEndedUtc < TimeSpan.FromMilliseconds(900))
        {
            lock (_utterance) { _utterance.SetLength(0); }
            _speechFrames = 0;
            return;
        }
        var playbackFrame = _speaking;
        double sum = 0;
        for (var index = 0; index + 1 < e.BytesRecorded; index += 2)
        {
            var sample = BitConverter.ToInt16(e.Buffer, index) / 32768d;
            sum += sample * sample;
        }
        var rms = Math.Sqrt(sum / Math.Max(1, e.BytesRecorded / 2));
        AudioLevelChanged?.Invoke(rms);
        if (DateTime.UtcNow - _lastLevelLogUtc > TimeSpan.FromSeconds(2)) { _lastLevelLogUtc = DateTime.UtcNow; AppLogger.Info($"Voice audio level rms={rms:F4}; speaking={_speaking}; processing={_processing}"); }
        // The microphone's measured room-noise floor is ~0.03-0.06 RMS;
        // 0.025 kept the utterance open forever and prevented ASR submission.
        var speech = rms >= (_speaking ? 0.18 : 0.08);
        if (speech)
        {
            if (_speechFrames == 0) StateChanged?.Invoke("检测到语音，正在录音…");
            _lastSpeechUtc = DateTime.UtcNow;
            _speechFrames++;
            if (_speaking)
            {
                _playbackSpeechFrames++;
                if (_playbackSpeechFrames >= 3)
                {
                    SpeechDetectedDuringPlayback?.Invoke();
                    CancelPlayback();
                    _playbackSpeechFrames = 0;
                }
            }
        }
        else if (_speaking) _playbackSpeechFrames = 0;
        // Never place playback audio into the next utterance. If a genuine
        // interruption was confirmed above, recording resumes on subsequent
        // frames after playback has been cancelled.
        if (playbackFrame || _speaking) return;
        if (_speechFrames > 0)
        {
            lock (_utterance) { _utterance.Write(e.Buffer, 0, e.BytesRecorded); }
            if (!_processing && _speechFrames >= 3 && DateTime.UtcNow - _lastSpeechUtc > TimeSpan.FromMilliseconds(520))
                _ = CompleteUtteranceAsync();
        }
    }

    private async Task CompleteUtteranceAsync()
    {
        byte[] pcm;
        lock (_utterance)
        {
            if (_processing) return;
            _processing = true;
            pcm = _utterance.ToArray();
            _utterance.SetLength(0);
            _speechFrames = 0;
        }
        _recognition?.Cancel();
        _recognition?.Dispose();
        _recognition = new CancellationTokenSource();
        try
        {
            StateChanged?.Invoke("正在识别语音…");
            AppLogger.Info($"Voice utterance captured; pcmBytes={pcm.Length}");
            var text = await RecognizeAsync(CreateWave(pcm), _recognition.Token).ConfigureAwait(false);
            AppLogger.Info($"Voice ASR completed; textLength={text.Length}");
            if (!string.IsNullOrWhiteSpace(text) && UtteranceRecognized is not null)
                await UtteranceRecognized.Invoke(text.Trim()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppLogger.Error("Full duplex voice recognition failed", exception);
            StateChanged?.Invoke($"语音识别失败：{exception.Message}");
        }
        finally { _processing = false; if (IsListening && !_speaking) StateChanged?.Invoke("正在聆听（说话可打断播报）"); }
    }

    private async Task<string> RecognizeAsync(byte[] wave, CancellationToken token)
    {
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wave);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "microphone.wav");
        form.Add(new StringContent(_asrModel), "model");
        form.Add(new StringContent("zh"), "language");
        using var response = await _http.PostAsync(_asrEndpoint, form, token).ConfigureAwait(false);
        AppLogger.Info($"Voice ASR request completed; status={(int)response.StatusCode}");
        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"ASR HTTP {(int)response.StatusCode}: {json}");
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
        return Regex.Replace(result, @"^language\s+[^<]+<asr_text>", "", RegexOptions.IgnoreCase).Trim();
    }

    public async Task SpeakAsync(string text, CancellationToken token)
    {
        CancelPlayback();
        StateChanged?.Invoke("正在生成语音…");
        using var request = new HttpRequestMessage(HttpMethod.Post, _ttsEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model = "sherpa-onnx-vits-zh-ll", input = text, voice = 0, speed = 1.05 }), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        AppLogger.Info($"Voice TTS request completed; status={(int)response.StatusCode}");
        var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"TTS HTTP {(int)response.StatusCode}: {Encoding.UTF8.GetString(bytes)}");
        await PlayAsync(bytes, token).ConfigureAwait(false);
    }

    private Task PlayAsync(byte[] wave, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new WaveFileReader(new MemoryStream(wave, writable: false));
        _output = new WaveOutEvent();
        _output.Init(reader);
        _output.PlaybackStopped += (_, args) =>
        {
            _speaking = false;
            _playbackEndedUtc = DateTime.UtcNow;
            reader.Dispose();
            _output?.Dispose(); _output = null;
            if (args.Exception is null) completion.TrySetResult(); else completion.TrySetException(args.Exception);
        };
        _speaking = true;
        StateChanged?.Invoke("正在播报（直接说话即可打断）");
        token.Register(CancelPlayback);
        _output.Play();
        return completion.Task;
    }

    public void CancelPlayback()
    {
        if (!_speaking) return;
        _speaking = false;
        try { _output?.Stop(); } catch { }
        StateChanged?.Invoke(IsListening ? "已打断，正在聆听" : "播报已停止");
    }

    private static byte[] CreateWave(byte[] pcm)
    {
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(stream), new WaveFormat(SampleRate, 16, 1))) writer.Write(pcm, 0, pcm.Length);
        return stream.ToArray();
    }

    public void Dispose() { Stop(); _http.Dispose(); _recognition?.Dispose(); _utterance.Dispose(); }
}
