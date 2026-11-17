using System.Net.Http.Json;
using Picer.Core.Identity;

namespace Picer.Core.Gateway;

/// <summary>Initial, safe portion of one-click setup. It never stores passwords or bypasses approval.</summary>
public sealed class ServiceBootstrapper(HttpClient httpClient) : IServiceBootstrapper
{
    public async Task<BootstrapLoginResponse> LoginAsync(
        string origin, string login, string password, string devicePublicKey, string deviceName,
        CancellationToken cancellationToken = default)
    {
        var normalized = DeviceAuthorization.NormalizeOrigin(origin);
        using var response = await httpClient.PostAsJsonAsync(normalized + "/api/bootstrap/login",
            new BootstrapLoginRequest(login, password, devicePublicKey, deviceName), DeviceAuthorization.Json, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new UnauthorizedAccessException("bootstrap_login_failed");
        return await response.Content.ReadFromJsonAsync<BootstrapLoginResponse>(DeviceAuthorization.Json, cancellationToken)
            ?? throw new InvalidDataException("bootstrap_login_empty");
    }

    public async Task<DeviceAuthorizationFile> EnrollAsync(
        string origin, string bootstrapToken, string requestId, string devicePublicKey, string deviceName, string clientKind,
        string spkiPin, CancellationToken cancellationToken = default)
    {
        var normalized = DeviceAuthorization.NormalizeOrigin(origin);
        using var transport = DeviceAccountClient.CreateTransport(spkiPin);
        using var client = new HttpClient(transport) { BaseAddress = new Uri(normalized), Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.PostAsJsonAsync("/api/bootstrap/enroll",
            new BootstrapEnrollRequest(bootstrapToken, requestId, devicePublicKey, deviceName, clientKind), DeviceAuthorization.Json, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new UnauthorizedAccessException("bootstrap_enroll_failed");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        try
        {
            // Accept the canonical .picer-auth envelope.
            return DeviceAuthorization.ReadFileDocument(bytes);
        }
        catch (InvalidDataException)
        {
            // Older deployed gateways returned the signed document itself.
            // Normalize it locally before handing it to DeviceAccountClient.
            var document = DeviceAuthorization.ReadDocument<DeviceAuthorizationFile>(bytes);
            if (string.IsNullOrWhiteSpace(document.Signature)) throw;
            return DeviceAuthorization.ReadFileDocument(DeviceAuthorization.WriteFileDocument(document));
        }
    }

    public async Task<EnrollmentStarted> BeginEnrollmentAsync(
        DeviceAccountClient client,
        IProgress<ServiceBootstrapProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new(ServiceBootstrapPhase.EnrollmentPending, "正在创建设备申请"));
        var enrollment = await client.BeginEnrollmentAsync(cancellationToken);
        progress?.Report(new(ServiceBootstrapPhase.EnrollmentPending, "等待用户批准设备"));
        return enrollment;
    }

    public async Task ImportAndVerifyAsync(
        DeviceAccountClient client,
        ReadOnlyMemory<byte> authenticationFile,
        IProgress<ServiceBootstrapProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new(ServiceBootstrapPhase.Imported, "正在导入设备认证"));
        await client.ImportAsync(authenticationFile, cancellationToken);
        progress?.Report(new(ServiceBootstrapPhase.Verifying, "正在验证驻留程序连接"));
        using var socket = await client.ConnectWebSocketAsync("api/agent/v1/connect", cancellationToken);
        socket.Abort();
        progress?.Report(new(ServiceBootstrapPhase.Applied, "设备已连接"));
    }

    public async Task<ServiceBootstrapResult> ConfigureAsync(
        ServiceBootstrapRequest request,
        IProgress<ServiceBootstrapProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var origin = DeviceAuthorization.NormalizeOrigin(request.ServiceAddress);
        progress?.Report(new(ServiceBootstrapPhase.Discovering, "正在发现服务"));
        ServiceDiscovery discovery;
        try
        {
            discovery = await httpClient.GetFromJsonAsync<ServiceDiscovery>(origin + "/identity/service", cancellationToken)
                ?? throw new InvalidDataException("service_discovery_empty");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            return new(false, ServiceBootstrapPhase.RolledBack, origin, null, "service_discovery_failed", exception.Message);
        }

        if (!string.Equals(DeviceAuthorization.NormalizeOrigin(discovery.Origin), origin, StringComparison.OrdinalIgnoreCase))
            return new(false, ServiceBootstrapPhase.RolledBack, origin, discovery.ServiceId, "service_origin_mismatch", "服务返回的 Origin 与输入地址不一致");

        progress?.Report(new(ServiceBootstrapPhase.EnrollmentPending, "等待用户批准设备"));
        return new(true, ServiceBootstrapPhase.EnrollmentPending, origin, discovery.ServiceId);
    }
}
