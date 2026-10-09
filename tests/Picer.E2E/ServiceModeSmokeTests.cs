using Picer.Core.Imaging;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Picer.Core.Gateway;

namespace Picer.E2E;

public sealed class ServiceModeSmokeTests
{
    private const string Token = "e2e-index-token";

    private static byte[] PngBytes()
    {
        using var image = new RasterImage(4, 3, new RgbPixel(120, 60, 30));
        using var encoded = new MemoryStream();
        image.SaveAsPng(encoded);
        return encoded.ToArray();
    }

    [Fact]
    public async Task ServiceProcess_EndpointDiscovery_AndGatewayRoundTrip()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "picer-e2e-svc-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = dataRoot + "-source";
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllBytes(Path.Combine(sourceRoot, "smoke.png"), PngBytes());

        var exe = Path.Combine(AppContext.BaseDirectory, "Picer.IndexService.exe");
        Assert.True(File.Exists(exe), $"服务可执行文件缺失：{exe}");
        var port = FreePort();

        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        startInfo.EnvironmentVariables["PICER_INDEX_DATA_ROOT"] = dataRoot;
        startInfo.EnvironmentVariables["PICER_INDEX_SERVICE_PORT"] = port.ToString();
        startInfo.EnvironmentVariables["PICER_INDEX_SERVICE_TOKEN"] = Token;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("服务进程启动失败。");
        var stderrTail = new StringBuilder();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            var endpointPath = Path.Combine(dataRoot, "service-endpoint.json");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(endpointPath))
            {
                if (process.HasExited)
                {
                    var tail = stderrTail.Length > 0 ? stderrTail.ToString() : "（无 stderr 输出）";
                    throw new InvalidOperationException($"服务进程提前退出：{process.ExitCode}。stderr 尾部：{tail}");
                }
                if (DateTime.UtcNow > deadline) throw new TimeoutException("service-endpoint.json 未在时限内生成。");
                await Task.Delay(250);
            }

            var backendPath = Path.Combine(dataRoot, "backend.json");
            await File.WriteAllTextAsync(backendPath, """{"mode":"service"}""");
            var backend = IndexBackendConfig.Load(backendPath, endpointPath, static cipher =>
                ProtectedData.Unprotect(cipher, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            Assert.Equal(IndexBackendConfig.ServiceMode, backend.Mode);
            Assert.NotNull(backend.Token);
            Assert.NotNull(backend.PipeName);

            using var pipeGateway = new PipeIndexGateway(backend.PipeName, backend.Token);
            var health = await pipeGateway.GetHealthAsync();
            Assert.Equal("ok", health.Status);

            using var httpGateway = new HttpIndexGateway(backend.BaseUrl!, backend.Token, backend.SpkiPin);
            Assert.Equal("ok", (await httpGateway.GetHealthAsync()).Status);

            var registered = await pipeGateway.RegisterSourceAsync(sourceRoot);
            var jobId = await pipeGateway.StartScanAsync(registered.Id);
            var scanDeadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                var job = await pipeGateway.GetScanJobAsync(jobId);
                if (job is { Done: true }) { Assert.Null(job.Error); break; }
                if (DateTime.UtcNow > scanDeadline) throw new TimeoutException("扫描未在时限内完成。");
                await Task.Delay(250);
            }

            var page = await pipeGateway.ListImagesAsync(sourceId: registered.Id, includeMissing: true);
            Assert.Equal(1, page.TotalCount);

            await httpGateway.RegisterSourceAsync(sourceRoot);
            Assert.Contains(await httpGateway.ListSourcesAsync(), source => source.Id == registered.Id);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            if (stderrTail.Length == 0)
                try { stderrTail.Append(await stderrTask); } catch { }
            try { Directory.Delete(dataRoot, true); } catch { }
            try { Directory.Delete(sourceRoot, true); } catch { }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
