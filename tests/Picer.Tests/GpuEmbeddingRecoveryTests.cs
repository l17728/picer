using System.Diagnostics;
using Picer.ModelServer;

namespace Picer.Tests;

public sealed class GpuEmbeddingRecoveryTests
{
    private static ProcessStartInfo Worker(string script)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(script);
        return info;
    }

    [Fact]
    public async Task TimeoutKillsStalledWorkerAndNextRequestStartsFresh()
    {
        var starts = 0;
        using var service = new GpuEmbeddingService(TimeSpan.FromSeconds(3), () =>
            Worker(Interlocked.Increment(ref starts) == 1
                ? "[Console]::ReadLine() | Out-Null; Start-Sleep -Seconds 60"
                : "[Console]::ReadLine() | Out-Null; [Console]::WriteLine('{}')"));
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
            return Worker("[Console]::ReadLine() | Out-Null; Start-Sleep -Seconds 60");
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
