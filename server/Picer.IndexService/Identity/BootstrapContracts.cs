using System.Text.Json.Serialization;

namespace Picer.IndexService.Identity;

internal sealed record BootstrapLoginRequest(
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("devicePublicKey")] string DevicePublicKey,
    [property: JsonPropertyName("deviceName")] string DeviceName);

internal sealed record BootstrapLoginResponse(
    [property: JsonPropertyName("bootstrapToken")] string BootstrapToken,
    [property: JsonPropertyName("expiresIn")] int ExpiresIn,
    [property: JsonPropertyName("serviceId")] string ServiceId);

internal sealed record BootstrapEnrollRequest(
    [property: JsonPropertyName("bootstrapToken")] string BootstrapToken,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("devicePublicKey")] string DevicePublicKey,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("clientKind")] string ClientKind);
