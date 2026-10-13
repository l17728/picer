using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Picer.Core.Identity;
using Picer.IndexService;
using Picer.IndexService.Identity;
using Xunit;

namespace Picer.Tests;

public sealed class AccountPortalTests
{
    [Fact]
    public async Task BrowserRegistrationDeviceFileActivationAndRevocationWorkOverHttps()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-portal-" + Guid.NewGuid().ToString("N"));
        const string origin = "https://picer.test";
        var instance = AccountServiceHost.Build(Path.Combine(root, "service"), origin, 0);
        try
        {
            var address = await IndexServiceHost.StartAsync(instance.App);
            var pin = ServiceSecurity.ComputeSpkiPin(instance.Certificate);
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null &&
                    CryptographicOperations.FixedTimeEquals(cert.PublicKey.ExportSubjectPublicKeyInfo(), pin)
            };
            using var http = new HttpClient(handler) { BaseAddress = new Uri(address) };
            http.DefaultRequestHeaders.Host = "picer.test";
            using (var versionResponse = await http.GetAsync("/version"))
            {
                versionResponse.EnsureSuccessStatusCode();
                using var version = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
                Assert.Equal("Picer.IndexService", version.RootElement.GetProperty("service").GetString());
                Assert.False(string.IsNullOrWhiteSpace(version.RootElement.GetProperty("assemblyVersion").GetString()));
                Assert.True(version.RootElement.GetProperty("startedUtc").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
            }
            var service = await http.GetFromJsonAsync<ServiceDiscovery>("/identity/service");
            Assert.NotNull(service);
            using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var requestId = Guid.NewGuid().ToString("N");
            var publicKey = DeviceAuthorization.ExportPublicKey(deviceKey);
            using (var enroll = await SignedPost(http, deviceKey, origin, "/identity/enrollments", new DeviceEnrollmentRequest(requestId, publicKey, "我的新电脑", "gui")))
                Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);

            using (var withoutCsrf = await http.PostAsync("/account/register", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["login"] = "example", ["password"] = "example-password-123!", ["confirm"] = "example-password-123!", ["space"] = "家庭照片" })))
                Assert.Equal(HttpStatusCode.BadRequest, withoutCsrf.StatusCode);
            var registration = await http.GetStringAsync("/account/register?request=" + requestId);
            using (var registered = await Submit(http, "/account/register", registration, new()
            { ["login"] = "example", ["password"] = "example-password-123!", ["confirm"] = "example-password-123!", ["space"] = "家庭照片", ["request"] = requestId }))
            {
                Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
                Assert.Equal("/account/approve?request=" + requestId, registered.Headers.Location?.OriginalString);
            }
            var dashboard = await http.GetStringAsync("/account");
            Assert.Contains("家庭照片", WebUtility.HtmlDecode(dashboard));
            var approval = await http.GetStringAsync("/account/approve?request=" + requestId);
            var spaceMatch = Regex.Match(approval, "<option value='([a-f0-9]{32})'>", RegexOptions.None, TimeSpan.FromSeconds(1));
            Assert.True(spaceMatch.Success);
            var space = spaceMatch.Groups[1].Value;
            DeviceAuthorizationFile document;
            using (var download = await Submit(http, "/account/approve", approval, new() { ["request"] = requestId, ["space"] = space }))
            {
                Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                Assert.Contains("picer-auth", download.Content.Headers.ContentDisposition?.FileName);
                document = DeviceAuthorization.ReadFileDocument(await download.Content.ReadAsByteArrayAsync());
            }
            using var signing = DeviceAuthorization.ReadPublicKey(service.SigningKey);
            DeviceAuthorization.ValidateFile(document, origin, service.ServiceId, requestId, publicKey, signing, DateTimeOffset.UtcNow);
            DeviceSession session;
            using (var redemption = await SignedPost(http, deviceKey, origin, "/identity/redeem", new EnrollmentRedemption(requestId, document.RedemptionCode, Guid.NewGuid().ToString("N"))))
            {
                Assert.Equal(HttpStatusCode.OK, redemption.StatusCode);
                session = (await redemption.Content.ReadFromJsonAsync<DeviceSession>())!;
            }
            using (var activation = await SignedPost(http, deviceKey, origin, "/identity/activate", new { }, session.AccessToken))
                Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
            using (var read = await SignedGet(http, deviceKey, origin, "/spaces/" + space + "/api/gateway/v1/images", session.AccessToken))
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using (var crossSpace = await SignedGet(http, deviceKey, origin, "/spaces/" + Guid.NewGuid().ToString("N") + "/api/gateway/v1/images", session.AccessToken))
                Assert.Equal(HttpStatusCode.Unauthorized, crossSpace.StatusCode);
            using (var administrative = await SignedGet(http, deviceKey, origin, "/spaces/" + space + "/api/service/v1/config", session.AccessToken))
                Assert.Equal(HttpStatusCode.Forbidden, administrative.StatusCode);
            dashboard = await http.GetStringAsync("/account");
            using (var revoked = await Submit(http, "/account/revoke", dashboard, new() { ["grant"] = session.GrantId }))
                Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
            using (var denied = await SignedGet(http, deviceKey, origin, "/spaces/" + space + "/health", session.AccessToken))
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var forgedHost = new HttpRequestMessage(HttpMethod.Get, "/account");
            forgedHost.Headers.Host = "untrusted.example";
            using var rejectedHost = await http.SendAsync(forgedHost);
            Assert.Equal(HttpStatusCode.BadRequest, rejectedHost.StatusCode);
        }
        finally
        {
            await instance.App.StopAsync(); await instance.App.DisposeAsync(); instance.Certificate.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static Task<HttpResponseMessage> Submit(HttpClient http, string path, string html, Dictionary<string, string> fields)
    {
        var match = Regex.Match(html, "name='(__RequestVerificationToken)' value='([^']+)'", RegexOptions.None, TimeSpan.FromSeconds(1));
        Assert.True(match.Success);
        fields[match.Groups[1].Value] = WebUtility.HtmlDecode(match.Groups[2].Value);
        return http.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    private static async Task<HttpResponseMessage> SignedPost(HttpClient http, ECDsa key, string origin, string path, object body, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("DPoP", DeviceProof.Create(key, "POST", new Uri(origin + path), DateTimeOffset.UtcNow, token));
        if (token is not null) request.Headers.Authorization = new("DPoP", token);
        return await http.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SignedGet(HttpClient http, ECDsa key, string origin, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("DPoP", DeviceProof.Create(key, "GET", new Uri(origin + path), DateTimeOffset.UtcNow, token));
        request.Headers.Authorization = new("DPoP", token);
        return await http.SendAsync(request);
    }
}
