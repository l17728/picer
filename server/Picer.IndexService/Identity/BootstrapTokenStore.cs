using System.Collections.Concurrent;

namespace Picer.IndexService.Identity;

internal sealed class BootstrapTokenStore(TimeProvider? clock = null)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Issue(string accountId, string publicKey, string deviceName)
    {
        var token = Picer.Core.Identity.DeviceAuthorization.NewSecret();
        _entries[token] = new(accountId, publicKey, deviceName, _clock.GetUtcNow().AddMinutes(2));
        return token;
    }

    public bool TryConsume(string token, string publicKey, out string accountId, out string deviceName)
    {
        accountId = string.Empty; deviceName = string.Empty;
        if (!_entries.TryRemove(token, out var entry) || entry.ExpiresUtc <= _clock.GetUtcNow() || entry.PublicKey != publicKey)
            return false;
        accountId = entry.AccountId; deviceName = entry.DeviceName; return true;
    }

    private sealed record Entry(string AccountId, string PublicKey, string DeviceName, DateTimeOffset ExpiresUtc);
}
