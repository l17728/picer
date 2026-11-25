using System.Diagnostics;
using System.Text.Json;

namespace Picer.ModelServer;

/// <summary>常驻 GPU worker，复用 Python 进程和 ONNX Session，避免每批重复加载模型。</summary>
public sealed class GpuEmbeddingService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _processGate = new();
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private bool _disposed;

    private readonly TimeSpan _timeout;
    private readonly Func<ProcessStartInfo>? _workerStartInfo;

    public GpuEmbeddingService() : this(TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("PICER_GPU_WORKER_TIMEOUT_SECONDS"), out var seconds)
            ? Math.Clamp(seconds, 1, 600) : 120)) { }

    public GpuEmbeddingService(TimeSpan timeout, Func<ProcessStartInfo>? workerStartInfo = null)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
        _workerStartInfo = workerStartInfo;
    }

    public async Task<IReadOnlyList<float[]>> GenerateAsync(string modelVersion, string[] images, CancellationToken ct = default)
    {
        if (images.Length == 0) return [];
        var result = await ExecuteAsync(new { operation = "embed", modelVersion, images }, ct).ConfigureAwait(false);
        if (!result.TryGetProperty("embeddings", out var values))
            throw new InvalidOperationException("GPU worker returned no embeddings");
        return values.EnumerateArray().Select(value => value.EnumerateArray().Select(item => item.GetSingle()).ToArray()).ToArray();
    }

    public Task<JsonElement> GetRuntimeAsync(CancellationToken ct = default) =>
        ExecuteAsync(new { operation = "runtime" }, ct);

    public Task<JsonElement> DetectFacesAsync(string modelVersion, string[] images, CancellationToken ct = default)
    {
        if (images.Length == 0) throw new ArgumentException("At least one image is required", nameof(images));
        return ExecuteAsync(new { operation = "detect", modelVersion, images }, ct);
    }

    private async Task<JsonElement> ExecuteAsync(object payload, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        var token = deadline.Token;
        var entered = false;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            try
            {
                var request = JsonSerializer.Serialize(payload);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        EnsureWorker(token);
                        await _input!.WriteLineAsync(request.AsMemory(), token).ConfigureAwait(false);
                        await _input.FlushAsync(token).ConfigureAwait(false);
                        var line = await _output!.ReadLineAsync(token).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(line)) throw new InvalidOperationException("GPU worker closed its response stream");
                        using var document = JsonDocument.Parse(line);
                        if (document.RootElement.TryGetProperty("error", out var error))
                            throw new InvalidOperationException(error.GetString() ?? "GPU worker failed");
                        return document.RootElement.Clone();
                    }
                    catch (OperationCanceledException) { StopWorker(); throw; }
                    catch when (attempt == 0) { StopWorker(); }
                    catch { StopWorker(); throw; }
                }
                throw new InvalidOperationException("GPU worker failed after restart");
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(entered ? "face_worker_timeout" : "face_worker_queue_timeout");
        }
    }

    private void EnsureWorker(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_processGate)
        {
            if (_process is { HasExited: false } && _input is not null && _output is not null) return;
            StopWorker();
            var python = Environment.GetEnvironmentVariable("PICER_GPU_WORKER_PYTHON");
            var script = Environment.GetEnvironmentVariable("PICER_GPU_WORKER_SCRIPT");
            if (_workerStartInfo is null && (string.IsNullOrWhiteSpace(python) || string.IsNullOrWhiteSpace(script)))
                throw new InvalidOperationException("GPU worker is not configured");
            var info = _workerStartInfo?.Invoke() ?? new ProcessStartInfo(python!) { RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            if (_workerStartInfo is null) { info.ArgumentList.Add(script!); info.ArgumentList.Add("--server"); }
            _process = Process.Start(info) ?? throw new InvalidOperationException("GPU worker could not start");
            _input = _process.StandardInput; _output = _process.StandardOutput;
            _ = DrainErrorsAsync(_process.StandardError);
        }
    }

    private static async Task DrainErrorsAsync(StreamReader error)
    {
        try { while (await error.ReadLineAsync().ConfigureAwait(false) is not null) { } }
        catch (ObjectDisposedException) { }
    }

    private void StopWorker()
    {
        lock (_processGate)
        {
            try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
            _input?.Dispose(); _output?.Dispose(); _process?.Dispose();
            _input = null; _output = null; _process = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; StopWorker(); _gate.Dispose();
    }
}
