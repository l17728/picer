using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace Picer.E2E;

public sealed class ModelServerContractE2ETests
{
    [Fact]
    [Trait("Category", "ServiceContract")]
    public async Task HealthQualityAuditAndRankingEndpoints_WorkTogether()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-model-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var port = FreePort();
        var exe = Path.Combine(AppContext.BaseDirectory, "Picer.ModelServer.exe");
        Assert.True(File.Exists(exe), $"ModelServer executable missing: {exe}");
        using var process = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            Environment = { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}", ["PICER_TRAINING_ARCHIVE_ROOT"] = root }
        });
        Assert.NotNull(process);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            await WaitForHealthAsync(client, process);
            var sample = new
            {
                snapshotId = "e2e-snapshot",
                samples = new[]
                {
                    new { faceId = 1L, personId = 1L, split = "train", label = "positive", embedding = new[] { 1f, 0f }, evidence = new { groupId = "g1", imageHash = "h1" } },
                    new { faceId = 2L, personId = 1L, split = "validation", label = "positive", embedding = new[] { .99f, .01f }, evidence = new { groupId = "g2", imageHash = "h2" } },
                    new { faceId = 3L, personId = 2L, split = "test", label = "positive", embedding = new[] { 0f, 1f }, evidence = new { groupId = "g3", imageHash = "h3" } },
                    new { faceId = 4L, personId = 2L, split = "train", label = "positive", embedding = new[] { .01f, .99f }, evidence = new { groupId = "g4", imageHash = "h4" } },
                    new { faceId = 5L, personId = 0L, split = "validation", label = "negative", embedding = new[] { .7f, .7f }, evidence = new { groupId = "g5", imageHash = "h5" } }
                }
            };
            using var quality = await client.PostAsJsonAsync("api/face-training/quality-report", sample);
            Assert.Equal(HttpStatusCode.OK, quality.StatusCode);
            using var qualityJson = JsonDocument.Parse(await quality.Content.ReadAsStringAsync());
            Assert.True(qualityJson.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal(5, qualityJson.RootElement.GetProperty("totalSamples").GetInt32());

            using var algorithms = await client.GetAsync("api/face-training/algorithms");
            Assert.Equal(HttpStatusCode.OK, algorithms.StatusCode);
            using var ranking = await client.GetAsync("api/face-training/model-rankings?scenario=e2e");
            Assert.Equal(HttpStatusCode.OK, ranking.StatusCode);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task WaitForHealthAsync(HttpClient client, Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException($"ModelServer exited with {process.ExitCode}");
            try
            {
                using var response = await client.GetAsync("health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(200);
        }
        throw new TimeoutException("ModelServer health endpoint did not become ready.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
