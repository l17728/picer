using System.Text.Json.Serialization;

namespace Picer.Core.Gateway;

public sealed record BootstrapLoginRequest(
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("devicePublicKey")] string DevicePublicKey,
    [property: JsonPropertyName("deviceName")] string DeviceName);

public sealed record BootstrapLoginResponse(
    [property: JsonPropertyName("bootstrapToken")] string BootstrapToken,
    [property: JsonPropertyName("expiresIn")] int ExpiresIn,
    [property: JsonPropertyName("serviceId")] string ServiceId);

public sealed record BootstrapEnrollRequest(
    [property: JsonPropertyName("bootstrapToken")] string BootstrapToken,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("devicePublicKey")] string DevicePublicKey,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("clientKind")] string ClientKind);
