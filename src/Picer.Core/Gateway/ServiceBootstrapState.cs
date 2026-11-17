namespace Picer.Core.Gateway;

/// <summary>States used by the one-click service configuration transaction.</summary>
public enum ServiceBootstrapPhase
{
    Idle,
    Discovering,
    Authenticating,
    EnrollmentPending,
    Imported,
    Verifying,
    Applied,
    RolledBack
}

public sealed record ServiceBootstrapProgress(
    ServiceBootstrapPhase Phase,
    string Message,
    string? ErrorCode = null);

public sealed record ServiceBootstrapRequest(
    string ServiceAddress,
    string UserName,
    string Password,
    IReadOnlyList<string> Roots);

public sealed record ServiceBootstrapResult(
    bool Success,
    ServiceBootstrapPhase Phase,
    string ServiceAddress,
    string? ServiceId,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public interface IServiceBootstrapper
{
    Task<ServiceBootstrapResult> ConfigureAsync(
        ServiceBootstrapRequest request,
        IProgress<ServiceBootstrapProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
