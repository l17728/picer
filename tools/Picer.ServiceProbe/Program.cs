using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Picer.Core.Gateway;
using Picer.Core.Identity;

// Load the gateway from the installed payload, not a source project reference.
// Credentials enter only through the environment and are never printed.
var stage = "configuration";
try
{
    if (args.Length != 1) throw new ArgumentException();
    var root = Path.GetFullPath(args[0]);
    var url = Environment.GetEnvironmentVariable("PICER_LIVE_SERVICE_URL") ?? "";
    var token = Environment.GetEnvironmentVariable("PICER_LIVE_SERVICE_TOKEN") ?? "";
    var accountLogin = Environment.GetEnvironmentVariable("PICER_LIVE_SERVICE_ACCOUNT") ?? "";
    var accountPassword = Environment.GetEnvironmentVariable("PICER_LIVE_SERVICE_PASSWORD") ?? "";
    var pin = Convert.FromBase64String(Environment.GetEnvironmentVariable("PICER_LIVE_SERVICE_SPKI_PIN") ?? "");
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
        uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 ||
        uri.Fragment.Length != 0 || pin.Length == 0 ||
        (string.IsNullOrWhiteSpace(token) == string.IsNullOrWhiteSpace(accountLogin)) ||
        (string.IsNullOrWhiteSpace(accountLogin) != string.IsNullOrWhiteSpace(accountPassword)))
        throw new ArgumentException();
    var corePath = Path.Combine(root, "Picer.Core.dll");
    var context = new AssemblyLoadContext("InstalledGateway", isCollectible: true);
    context.Resolving += (_, name) =>
    {
        var candidate = Path.Combine(root, name.Name + ".dll");
        return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
    };
    var assembly = context.LoadFromAssemblyPath(corePath);
    var gatewayType = assembly.GetType("Picer.Core.Gateway.HttpIndexGateway", throwOnError: true)!;
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    stage = "certificate-discovery";
    var settingsType = assembly.GetType("Picer.Core.Gateway.MediaAgentSettings", throwOnError: true)!;
    var discovery = (Task<byte[]>)settingsType.GetMethod("DiscoverSpkiAsync")!.Invoke(null, [url, deadline.Token])!;
    if (!CryptographicOperations.FixedTimeEquals(await discovery, pin)) throw new InvalidOperationException();
    using var handler = new HttpClientHandler { AllowAutoRedirect = false };
    handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
        CryptographicOperations.FixedTimeEquals(certificate.PublicKey.ExportSubjectPublicKeyInfo(), pin);
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(130) };
    DeviceAccountClient? accountClient = null;
    ECDsa? accountKey = null;
    HttpClient? accountPortal = null;
    IDisposable gateway;
    if (!string.IsNullOrWhiteSpace(accountLogin))
    {
        stage = "account-device-pairing";
        var cookieJar = new CookieContainer();
        var portalHandler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookieJar };
        portalHandler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
            CryptographicOperations.FixedTimeEquals(certificate.PublicKey.ExportSubjectPublicKeyInfo(), pin);
        accountPortal = new HttpClient(portalHandler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
        var portal = accountPortal;
        using var discoveryResponse = await portal.GetAsync("identity/service", deadline.Token);
        discoveryResponse.EnsureSuccessStatusCode();
        var serviceDiscovery = await discoveryResponse.Content.ReadFromJsonAsync<ServiceDiscovery>(DeviceAuthorization.Json, deadline.Token)
            ?? throw new InvalidDataException();
        if (serviceDiscovery.Origin != url || serviceDiscovery.SpkiPin != Convert.ToBase64String(pin)) throw new InvalidDataException();
        accountKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var connection = new DeviceConnection(url, serviceDiscovery.ServiceId, serviceDiscovery.SpkiPin, serviceDiscovery.SigningKey,
            "picer-live-ci", Guid.NewGuid().ToString("N"), "Picer GitHub CI probe", "gui");
        accountClient = new DeviceAccountClient(connection, accountKey, _ => { }, handler);
        var enrollment = await accountClient.BeginEnrollmentAsync(deadline.Token);
        if (enrollment.ApprovalUrl != url + "/account/approve?request=" + connection.RequestId) throw new InvalidDataException();

        async Task<(string Token, string Html)> ReadForm(string path)
        {
            using var response = await portal.GetAsync(path, deadline.Token);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(deadline.Token);
            var match = Regex.Match(html, "<input[^>]*name=['\"](?<name>[^'\"]+)['\"][^>]*value=['\"](?<value>[^'\"]+)['\"][^>]*>", RegexOptions.IgnoreCase);
            if (!match.Success) throw new InvalidDataException();
            return (System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value), html);
        }
        var (loginToken, _) = await ReadForm("account/login?request=" + connection.RequestId);
        using (var login = await portal.PostAsync("account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = loginToken, ["request"] = connection.RequestId, ["login"] = accountLogin, ["password"] = accountPassword }), deadline.Token))
            if ((int)login.StatusCode is < 300 or >= 400) throw new InvalidDataException();

        var approvalPath = "account/approve?request=" + connection.RequestId;
        var (approvalToken, approvalHtml) = await ReadForm(approvalPath);
        var spaceMatch = Regex.Match(approvalHtml, "<option value=['\"](?<space>[0-9a-f]{32})['\"]>", RegexOptions.IgnoreCase);
        if (!spaceMatch.Success) throw new InvalidDataException();
        using var approved = await portal.PostAsync("account/approve", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = approvalToken, ["request"] = connection.RequestId,
            ["space"] = spaceMatch.Groups["space"].Value
        }), deadline.Token);
        approved.EnsureSuccessStatusCode();
        await accountClient.ImportAsync(await approved.Content.ReadAsByteArrayAsync(deadline.Token), deadline.Token);
        gateway = (IDisposable)Activator.CreateInstance(gatewayType, accountClient.CreateSpaceClient(), true)!;
    }
    else gateway = (IDisposable)Activator.CreateInstance(gatewayType, url, token, pin, client)!;
    using var gatewayLifetime = gateway;
    using var accountLifetime = accountClient;
    using var accountKeyLifetime = accountKey;
    using var accountPortalLifetime = accountPortal;

    async Task<object?> Invoke(string method, params object?[] parameters)
    {
        var task = (Task)gatewayType.GetMethod(method)!.Invoke(gateway, parameters)!;
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    stage = "pinned-health";
    var health = await Invoke("GetHealthAsync", deadline.Token);
    if (!string.Equals(health?.GetType().GetProperty("Status")?.GetValue(health)?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException();
    static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
    static object[] Items(object page) => ((IEnumerable)Property(page, "Items")!).Cast<object>().ToArray();
    static long Id(object item) => Convert.ToInt64(Property(item, "Id"));
    static void ValidatePage(object page, object[] items)
    {
        if (items.Length is 0 or > 8 || Convert.ToInt32(Property(page, "TotalCount")) < items.Length ||
            items.Any(item => Id(item) <= 0) || items.Select(Id).Distinct().Count() != items.Length)
            throw new InvalidDataException();
    }
    async Task ValidateThumbnail(string method, object[] candidates, bool video)
    {
        foreach (var item in candidates)
        {
            stage = video ? "video-thumbnail-fetch" : "photo-thumbnail-fetch";
            using var stream = (Stream?)await Invoke(method, video ? [Id(item), null, deadline.Token] : [Id(item), deadline.Token]);
            if (stream is null) continue;
            stage = video ? "video-thumbnail-read" : "photo-thumbnail-read";
            using var bytes = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (bytes.Length + read > 4 * 1024 * 1024) throw new InvalidDataException();
                bytes.Write(buffer, 0, read);
            }
            Exception? decodeFailure = null;
            stage = video ? "video-thumbnail-decode" : "photo-thumbnail-decode";
            var thread = new Thread(() =>
            {
                try
                {
                    bytes.Position = 0;
                    var frame = BitmapDecoder.Create(bytes, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    if (frame.PixelWidth is <= 0 or > 4096 || frame.PixelHeight is <= 0 or > 4096) throw new InvalidDataException();
                    var stride = checked((frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8);
                    var length = checked(stride * frame.PixelHeight);
                    if (length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException();
                    frame.CopyPixels(new byte[length], stride, 0);
                }
                catch (Exception error) { decodeFailure = error; }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(10)) || decodeFailure is not null) throw new InvalidDataException();
            return;
        }
        throw new InvalidDataException();
    }
    stage = "photo-index-page";
    var page = (await Invoke("ListImagesAsync", null, false, 8, null, deadline.Token))!;
    var photos = Items(page);
    ValidatePage(page, photos);
    var nextCursor = (string?)Property(page, "NextCursor");
    if (!string.IsNullOrEmpty(nextCursor))
    {
        stage = "photo-index-next-page";
        var next = (await Invoke("ListImagesAsync", null, false, 8, nextCursor, deadline.Token))!;
        var nextItems = Items(next);
        ValidatePage(next, nextItems);
        if (nextItems.Select(Id).Intersect(photos.Select(Id)).Any()) throw new InvalidDataException();
    }
    stage = "photo-detail";
    var detail = (await Invoke("GetImageAsync", Id(photos[0]), deadline.Token))!;
    if (Id(detail) != Id(photos[0])) throw new InvalidDataException();
    stage = "photo-thumbnail-decode";
    await ValidateThumbnail("OpenThumbnailAsync", photos, false);
    stage = "video-index-page";
    var videoPage = (await Invoke("ListVideosAsync", null, 8, null, deadline.Token))!;
    var videos = Items(videoPage);
    ValidatePage(videoPage, videos);
    stage = "video-thumbnail-decode";
    await ValidateThumbnail("OpenVideoThumbnailAsync", videos, true);
    var mediaRangesVerified = false;
    if (gatewayType.GetMethod("GetMediaInfoAsync") is not null)
    {
        foreach (var video in videos)
        {
            stage = "video-range-metadata";
            object info;
            try { info = (await Invoke("GetMediaInfoAsync", Id(video), true, deadline.Token))!; }
            catch (HttpRequestException) { continue; }
            if (!(bool)Property(info, "SupportsStreaming")!) break;
            var length = Convert.ToInt64(Property(info, "Length"));
            try
            {
                if (length <= 0) continue;
                stage = "video-first-and-tail-range";
                var count = (int)Math.Min(4096, length);
                foreach (var offset in new[] { 0L, length - count })
                {
                    using var stream = (Stream)(await Invoke("OpenMediaRangeAsync", Id(video), true, info, offset, count, deadline.Token))!;
                    await stream.ReadExactlyAsync(new byte[count].AsMemory(), deadline.Token);
                }
                mediaRangesVerified = true;
                break;
            }
            finally
            {
                using var release = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await Invoke("ManageMediaLeaseAsync", Property(info, "ETag"), true, release.Token);
            }
        }
    }
    if (Environment.GetEnvironmentVariable("PICER_REQUIRE_STREAMING") == "1" && !mediaRangesVerified)
        throw new InvalidDataException();
    if (accountPortal is not null && accountClient?.Connection.Session is { } session)
    {
        stage = "account-probe-device-revocation";
        using var accountPage = await accountPortal.GetAsync("account", deadline.Token);
        accountPage.EnsureSuccessStatusCode();
        var accountHtml = await accountPage.Content.ReadAsStringAsync(deadline.Token);
        var article = Regex.Matches(accountHtml, "<article>(?<body>.*?)</article>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Cast<Match>().FirstOrDefault(match => match.Groups["body"].Value.Contains("Picer GitHub CI probe", StringComparison.Ordinal));
        if (article is null) throw new InvalidDataException();
        var grant = Regex.Match(article.Groups["body"].Value, "name=['\"]grant['\"] value=['\"](?<id>[0-9a-f]{32})['\"]", RegexOptions.IgnoreCase);
        var csrf = Regex.Match(accountHtml, "name=['\"]__RequestVerificationToken['\"] value=['\"](?<value>[^'\"]+)['\"]", RegexOptions.IgnoreCase);
        if (!grant.Success || !csrf.Success || grant.Groups["id"].Value != session.GrantId) throw new InvalidDataException();
        using var revoked = await accountPortal.PostAsync("account/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = System.Net.WebUtility.HtmlDecode(csrf.Groups["value"].Value), ["grant"] = session.GrantId }), deadline.Token);
        if ((int)revoked.StatusCode is < 300 or >= 400) throw new InvalidDataException();
    }
    stage = "invalid-token-rejected";
    var rejectedAddress = accountClient?.Connection.Session is { } currentSession
        ? new Uri(DeviceAuthorization.SpaceAddress(url, currentSession.SpaceId) + "/api/gateway/v1/images?pageSize=1")
        : new Uri(uri, "api/gateway/v1/images?pageSize=1");
    using var rejectedRequest = new HttpRequestMessage(HttpMethod.Get, rejectedAddress);
    rejectedRequest.Headers.Authorization = new("Bearer", "invalid-probe-" + Guid.NewGuid().ToString("N"));
    using var rejectedResponse = await client.SendAsync(rejectedRequest, deadline.Token);
    if (rejectedResponse.StatusCode != HttpStatusCode.Unauthorized) throw new InvalidOperationException();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        success = true, certificateDiscovery = true, pinnedTls = true, authenticatedRead = true, invalidTokenRejected = true,
        photoIndex = true, photoDetail = true, photoThumbnailDecoded = true, videoIndex = true, videoThumbnailDecoded = true,
        mediaRangesVerified,
        installedCoreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corePath))).ToLowerInvariant(),
        checkedUtc = DateTime.UtcNow
    }));
    return 0;
}
catch (Exception error)
{
    // Exception messages and HTTP bodies may contain private endpoint/media data.
    Console.Error.WriteLine("Live service verification failed at stage: " + stage);
    while (error.InnerException is not null) error = error.InnerException;
    Console.Error.WriteLine("Failure type: " + error.GetType().Name);
    return 1;
}
