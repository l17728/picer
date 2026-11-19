namespace Picer.Core.Gateway;

public enum IndexModeSwitchState
{
    Active,
    Preparing,
    Syncing,
    Switching,
    Cleanup,
    Completed,
    Failed
}

/// <summary>Guards an index-source switch. Data movement and cleanup are performed by the caller.</summary>
public sealed class IndexModeSwitchTransaction(string sourceMode, string targetMode)
{
    public string SourceMode { get; } = RequireMode(sourceMode);
    public string TargetMode { get; } = RequireMode(targetMode);
    public IndexModeSwitchState State { get; private set; } = IndexModeSwitchState.Active;
    public string? FailureCode { get; private set; }

    public void Begin()
    {
        Require(IndexModeSwitchState.Active);
        State = IndexModeSwitchState.Preparing;
    }

    public void BeginSync()
    {
        Require(IndexModeSwitchState.Preparing);
        State = IndexModeSwitchState.Syncing;
    }

    public void MarkSyncAndConsistencyPassed()
    {
        Require(IndexModeSwitchState.Syncing);
        State = IndexModeSwitchState.Switching;
    }

    public void MarkSwitched()
    {
        Require(IndexModeSwitchState.Switching);
        State = IndexModeSwitchState.Cleanup;
    }

    public void MarkCleanupCompleted()
    {
        Require(IndexModeSwitchState.Cleanup);
        State = IndexModeSwitchState.Completed;
    }

    public void Fail(string failureCode)
    {
        if (State is IndexModeSwitchState.Completed or IndexModeSwitchState.Failed)
            throw new InvalidOperationException("index_mode_switch_terminal");
        if (string.IsNullOrWhiteSpace(failureCode) || failureCode.Length > 64)
            throw new ArgumentException("invalid_failure_code", nameof(failureCode));
        FailureCode = failureCode;
        State = IndexModeSwitchState.Failed;
    }

    private void Require(IndexModeSwitchState expected)
    {
        if (State != expected) throw new InvalidOperationException($"index_mode_switch_state:{State}->{expected}");
    }

    private static string RequireMode(string value)
    {
        if (!string.Equals(value, IndexBackendConfig.LocalMode, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, IndexBackendConfig.ServiceMode, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("invalid_index_mode", nameof(value));
        return value.ToLowerInvariant();
    }
}
