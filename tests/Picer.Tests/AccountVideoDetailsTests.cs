using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Identity;
using Picer.Core.Video;
using Picer.IndexService;
using Picer.IndexService.Identity;

namespace Picer.Tests;

public sealed class AccountVideoDetailsTests
{
    [Fact]
    public async Task IndexReaderCanReadSelectedVideoThroughAccountGatewayWithoutMediaOrAnalysisGrant()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-video-account-" + Guid.NewGuid().ToString("N"));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var origin = $"https://127.0.0.1:{port}";
        var instance = AccountServiceHost.Build(Path.Combine(root, "service"), origin, port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await IndexServiceHost.StartAsync(instance.App);
            var account = instance.Accounts.Register("video-owner", "test-password-123!", "测试空间");
            var space = Assert.Single(instance.Accounts.ListSpaces(account));
            using var signing = IdentitySigningKey.LoadOrCreate(instance.Accounts.Root, instance.Accounts.ServiceId);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = Guid.NewGuid().ToString("N");
            instance.Accounts.CreateEnrollment(new(request, DeviceAuthorization.ExportPublicKey(key), "测试设备", "gui"));
            var pin = Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            var document = instance.Accounts.ApproveEnrollment(account, request, space.Id, origin, pin, signing, false, false);
            var connection = new DeviceConnection(origin, instance.Accounts.ServiceId, pin, DeviceAuthorization.ExportPublicKey(signing), "test", request, "gui", "gui");
            using var client = new DeviceAccountClient(connection, key, value => connection = value);
            await client.ImportAsync(DeviceAuthorization.WriteFileDocument(document), deadline.Token);
            long id;
            using (var db = new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id), "picer.db")))
            {
                var source = db.AddSource(root);
                var path = Path.Combine(root, "fixture.mp4"); await File.WriteAllBytesAsync(path, [1, 2, 3], deadline.Token);
                var videos = new VideoRepository(db);
                id = videos.Upsert(source.Id, path).Id;
                videos.SaveTranscript(id, "fixture", "selected transcript");
                videos.SaveKeyframes(id, [new VideoKeyframe(1, "fixture-frame")]);
            }
            var details = await client.SpaceClient.GetFromJsonAsync<VideoDetailsSnapshot>($"api/gateway/v1/videos/{id}/details?model=fixture", deadline.Token);
            Assert.NotNull(details);
            Assert.Equal(id, details.Video.Id);
            Assert.Equal("selected transcript", details.Transcript);
            Assert.Single(details.Frames);
            using var missing = await client.SpaceClient.GetAsync($"api/gateway/v1/videos/{long.MaxValue}/details", deadline.Token);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var rejected = await client.SpaceClient.PostAsync($"api/gateway/v1/videos/{id}/details", null, deadline.Token);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        }
        finally
        {
            await deadline.CancelAsync();
            await instance.App.StopAsync(); await instance.App.DisposeAsync(); instance.Certificate.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
