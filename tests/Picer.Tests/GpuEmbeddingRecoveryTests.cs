using System.Diagnostics;
using Picer.ModelServer;

namespace Picer.Tests;

public sealed class GpuEmbeddingRecoveryTests
{
    private static ProcessStartInfo Worker(bool respond = false)
    {
        var info = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        info.ArgumentList.Add("/d");
        info.ArgumentList.Add("/q");
        info.ArgumentList.Add("/c");
        // The stalled fixture waits on its still-open input pipe after consuming the request.
        info.ArgumentList.Add(respond ? "set /p request= & echo {}" : "set /p request= & set /p waiting=");
        return info;
    }

    [Fact]
    public async Task TimeoutKillsStalledWorkerAndNextRequestStartsFresh()
    {
        var starts = 0;
        using var service = new GpuEmbeddingService(TimeSpan.FromSeconds(3), () =>
            Worker(respond: Interlocked.Increment(ref starts) > 1));
        await Assert.ThrowsAsync<TimeoutException>(() => service.GetRuntimeAsync());
        var result = await service.GetRuntimeAsync();
        Assert.Equal(System.Text.Json.JsonValueKind.Object, result.ValueKind);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task CancellationDoesNotRetryAndDoesNotBlockTheGate()
    {
        var starts = 0;
        using var service = new GpuEmbeddingService(TimeSpan.FromSeconds(10), () =>
        {
            Interlocked.Increment(ref starts);
            return Worker();
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetRuntimeAsync(cancel.Token));
        Assert.Equal(1, starts);
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetRuntimeAsync(alreadyCanceled.Token));
        Assert.Equal(1, starts);
    }
}
