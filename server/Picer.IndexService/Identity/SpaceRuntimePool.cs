using System.Net.Http.Headers;
using System.Security.Cryptography;
using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

/// <summary>Each space owns its complete legacy engine closure, database, queues and caches behind an authenticated loopback boundary.</summary>
public sealed class SpaceRuntimePool(AccountStore accounts, int capacity = 32, RestoreGuard? restoreGuard = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Runtime> _spaces = new(StringComparer.Ordinal);
    private bool _disposed;

    public async Task<Lease> AcquireAsync(string spaceId, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)) throw new SpaceCapacityException();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_spaces.TryGetValue(spaceId, out var runtime))
            {
                if (_spaces.Count >= capacity)
                {
                    var idle = _spaces.Values.Where(item => item.Leases == 0).OrderBy(item => item.LastUse).FirstOrDefault();
                    if (idle is null) throw new SpaceCapacityException();
                    _spaces.Remove(idle.SpaceId);
                    await idle.DisposeAsync();
                }
                var secret = DeviceAuthorization.NewSecret();
                var instance = IndexServiceHost.Build(accounts.SpaceRoot(spaceId), 0, secret, isolatedSpace: true);
                try
                {
                    if (restoreGuard is not null)
                    {
                        using var database = instance.Handlers.OpenSpaceConnection();
                        new SpacePurgeCoordinator(database, restoreGuard, spaceId).Recover();
                    }
                    var address = await IndexServiceHost.StartAsync(instance.App);
                    var pin = ServiceSecurity.ComputeSpkiPin(instance.Certificate);
                    var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false,
                        MaxConnectionsPerServer = 32, PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
                    handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null &&
                        CryptographicOperations.FixedTimeEquals(certificate.GetPublicKey(), instance.Certificate.GetPublicKey());
                    var client = new HttpClient(handler) { BaseAddress = new Uri(address), Timeout = Timeout.InfiniteTimeSpan };
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                    runtime = new(spaceId, instance, client, secret, pin);
                    _spaces.Add(spaceId, runtime);
                }
                catch { await instance.App.DisposeAsync(); instance.Handlers.DisposeDatabase(); instance.Certificate.Dispose(); throw; }
            }
            runtime.Leases++;
            return new Lease(runtime, this);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask ReleaseAsync(Runtime runtime)
    {
        await _gate.WaitAsync();
        try { runtime.Leases--; runtime.LastUse = DateTimeOffset.UtcNow; }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var runtime in _spaces.Values) await runtime.DisposeAsync();
            _spaces.Clear();
        }
        finally { _gate.Release(); }
    }

    public sealed class Runtime(string spaceId, IndexServiceInstance instance, HttpClient client, string secret, byte[] pin) : IAsyncDisposable
    {
        public string SpaceId { get; } = spaceId;
        public HttpClient Client { get; } = client;
        public Picer.Core.Data.PicerDatabase OpenDatabase() => instance.Handlers.OpenSpaceConnection();
        public SemaphoreSlim MutationGate { get; } = new(1, 1);
        private readonly object _readersGate = new();
        private CancellationTokenSource _readers = new();
        private bool _purging;
        public bool Purging { get => Volatile.Read(ref _purging); set => Volatile.Write(ref _purging, value); }
        public CancellationToken DataLifetime { get { lock (_readersGate) return _readers.Token; } }
        public void CancelDataReaders()
        {
            CancellationTokenSource old;
            lock (_readersGate) { old = _readers; _readers = new(); }
            try { old.Cancel(); } finally { old.Dispose(); }
        }
        public Task PrepareIndexPurgeAsync(string kind, long id, string? agent, CancellationToken token)
            => instance.PrepareIndexPurgeAsync?.Invoke(kind, id, agent, token) ?? Task.CompletedTask;
        internal string Secret { get; } = secret;
        internal byte[] Pin { get; } = pin;
        internal int Leases { get; set; }
        internal DateTimeOffset LastUse { get; set; } = DateTimeOffset.UtcNow;
        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await instance.App.StopAsync(deadline.Token);
            await instance.App.DisposeAsync();
            Client.Dispose(); instance.Certificate.Dispose(); MutationGate.Dispose(); _readers.Dispose();
        }
    }

    public sealed class Lease(Runtime runtime, SpaceRuntimePool owner) : IAsyncDisposable
    {
        public Runtime Runtime { get; } = runtime;
        private int _released;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _released, 1) == 0 ? owner.ReleaseAsync(Runtime) : ValueTask.CompletedTask;
    }
}

public sealed class SpaceCapacityException : Exception;
