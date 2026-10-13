namespace Picer.IndexService.Identity;

/// <summary>Control/media fencing assumes one service process per root. A stale lock file does not prevent restart after process exit.</summary>
public sealed class AccountRuntimeLease : IDisposable
{
    private readonly FileStream _lease;
    public AccountRuntimeLease(string root)
    {
        Directory.CreateDirectory(root);
        try { _lease = new FileStream(Path.Combine(root, "service-runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw new IOException("account_service_root_already_in_use", exception); }
    }
    public void Dispose() => _lease.Dispose();
}
