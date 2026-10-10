using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

// Load the gateway from the installed payload, not a source project reference.
// Credentials enter only through the environment and are never printed.
var stage = "configuration";
try
{
    if (args.Length != 1) throw new ArgumentException();
    var root = Path.GetFullPath(args[0]);
    var url = Environment.GetEnvironmentVariable("PICER_LIVE_SERVICE_URL") ?? "";
    var token = Environment.GetEnvironmentVariable("PICER_LIVE_SERVICE_TOKEN") ?? "";
    var pin = Convert.FromBase64String(Environment.GetEnvironmentVariable("PICER_LIVE_SERVICE_SPKI_PIN") ?? "");
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
        uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 ||
        uri.Fragment.Length != 0 || string.IsNullOrWhiteSpace(token) || pin.Length == 0)
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
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    using var handler = new HttpClientHandler { AllowAutoRedirect = false };
    handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
        CryptographicOperations.FixedTimeEquals(certificate.PublicKey.ExportSubjectPublicKeyInfo(), pin);
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    using var gateway = (IDisposable)Activator.CreateInstance(gatewayType, url, token, pin, client)!;

    async Task<object?> Invoke(string method, params object?[] parameters)
    {
        var task = (Task)gatewayType.GetMethod(method)!.Invoke(gateway, parameters)!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    stage = "pinned-health";
    var health = await Invoke("GetHealthAsync", deadline.Token);
    if (!string.Equals(health?.GetType().GetProperty("Status")?.GetValue(health)?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException();
    stage = "authenticated-read";
    _ = await Invoke("ListImagesAsync", null, false, 1, null, deadline.Token);
    stage = "invalid-token-rejected";
    using var rejectedRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "api/gateway/v1/images?pageSize=1"));
    rejectedRequest.Headers.Authorization = new("Bearer", "invalid-probe-" + Guid.NewGuid().ToString("N"));
    using var rejectedResponse = await client.SendAsync(rejectedRequest, deadline.Token);
    if (rejectedResponse.StatusCode != HttpStatusCode.Unauthorized) throw new InvalidOperationException();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        success = true, pinnedTls = true, authenticatedRead = true, invalidTokenRejected = true,
        installedCoreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corePath))).ToLowerInvariant(),
        checkedUtc = DateTime.UtcNow
    }));
    return 0;
}
catch
{
    // Exception messages and HTTP bodies may contain private endpoint/media data.
    Console.Error.WriteLine("Live service verification failed at stage: " + stage);
    return 1;
}
