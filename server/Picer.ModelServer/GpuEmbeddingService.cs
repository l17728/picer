using System.Diagnostics;
using System.Text.Json;

namespace Picer.ModelServer;

public sealed class GpuEmbeddingService
{
    public async Task<IReadOnlyList<float[]>> GenerateAsync(string modelVersion, string[] images, CancellationToken ct = default)
    {
        var python = Environment.GetEnvironmentVariable("PICER_GPU_WORKER_PYTHON");
        var script = Environment.GetEnvironmentVariable("PICER_GPU_WORKER_SCRIPT");
        if (string.IsNullOrWhiteSpace(python) || string.IsNullOrWhiteSpace(script))
            throw new InvalidOperationException("GPU worker is not configured");
        using var process = Process.Start(new ProcessStartInfo(python, $"\"{script}\"")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("GPU worker could not start");
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { operation = "embed", modelVersion, images }));
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("embeddings", out var values)) throw new InvalidOperationException("GPU worker returned no embeddings");
        return values.EnumerateArray().Select(value => value.EnumerateArray().Select(item => item.GetSingle()).ToArray()).ToArray();
    }
}
