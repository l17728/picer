using System.Diagnostics;
using System.Text;

/// <summary>
/// Keeps one Python worker alive so CUDA is initialized once per ModelServer,
/// rather than once per training job. Access is serialized because the worker
/// protocol is line-oriented; a timeout or malformed response forces a clean
/// process restart before the next job.
/// </summary>
public sealed class GpuTrainingWorkerHost : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;

    public async Task<string> ExecuteAsync(string payload, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var process = await EnsureStartedAsync(cancellationToken);
            await _input!.WriteLineAsync(payload.AsMemory(), cancellationToken);
            await _input.FlushAsync(cancellationToken);
            var readTask = _output!.ReadLineAsync(cancellationToken).AsTask();
            var completed = await Task.WhenAny(readTask, Task.Delay(timeout, cancellationToken));
            if (completed != readTask)
            {
                Restart();
                throw new TimeoutException($"GPU worker timed out after {timeout.TotalSeconds:0}s");
            }

            var line = await readTask;
            if (string.IsNullOrWhiteSpace(line))
            {
                Restart();
                throw new InvalidOperationException("GPU worker closed its response stream");
            }
            return line;
        }
        catch
        {
            // A protocol error can leave one unread line in the stream even
            // when the process is still alive; never reuse that state.
            Restart();
            throw;
        }
        finally { _gate.Release(); }
    }

    private Task<Process> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }) return Task.FromResult(_process);
        Restart();
        var python = Environment.GetEnvironmentVariable("PICER_GPU_WORKER_PYTHON");
        var script = Environment.GetEnvironmentVariable("PICER_GPU_WORKER_SCRIPT");
        if (string.IsNullOrWhiteSpace(python) || string.IsNullOrWhiteSpace(script))
            throw new InvalidOperationException("GPU worker is not configured");

        var info = new ProcessStartInfo(python, $"\"{script}\" --server")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        _process = Process.Start(info) ?? throw new InvalidOperationException("GPU worker could not start");
        _input = _process.StandardInput;
        _output = _process.StandardOutput;
        _ = DrainErrorsAsync(_process.StandardError, cancellationToken);
        return Task.FromResult(_process);
    }

    private static async Task DrainErrorsAsync(StreamReader errors, CancellationToken cancellationToken)
    {
        try { while (await errors.ReadLineAsync(cancellationToken) is not null) { } }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void Restart()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
        _input = null;
        _output = null;
    }

    public void Dispose()
    {
        Restart();
        _gate.Dispose();
    }
}
