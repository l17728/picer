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

    public async Task<IReadOnlyList<float[]>> GenerateAsync(string modelVersion, string[] images, CancellationToken ct = default)
    {
        if (images.Length == 0) return [];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var request = JsonSerializer.Serialize(new { operation = "embed", modelVersion, images });
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    EnsureWorker(ct);
                    await _input!.WriteLineAsync(request.AsMemory(), ct).ConfigureAwait(false);
                    await _input.FlushAsync(ct).ConfigureAwait(false);
                    var line = await _output!.ReadLineAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line)) throw new InvalidOperationException("GPU worker closed its response stream");
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("error", out var error))
                        throw new InvalidOperationException(error.GetString() ?? "GPU worker failed");
                    if (!document.RootElement.TryGetProperty("embeddings", out var values))
                        throw new InvalidOperationException("GPU worker returned no embeddings");
                    return values.EnumerateArray().Select(value => value.EnumerateArray().Select(item => item.GetSingle()).ToArray()).ToArray();
                }
                catch when (attempt == 0) { StopWorker(); }
            }
            throw new InvalidOperationException("GPU worker failed after restart");
        }
        finally { _gate.Release(); }
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
            if (string.IsNullOrWhiteSpace(python) || string.IsNullOrWhiteSpace(script))
                throw new InvalidOperationException("GPU worker is not configured");
            var info = new ProcessStartInfo(python) { RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(script); info.ArgumentList.Add("--server");
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
