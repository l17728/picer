using Picer.Core.Logging;

namespace Picer.Core.Gateway;

/// <summary>Identity-bound blocks shared by seeks, prefetch and download; reconnects never mix source versions.</summary>
public sealed class ParallelMediaReader(IRangeMediaGateway gateway, long id, bool video, RemoteMediaInfo info, int concurrency = 2) : IAsyncDisposable
{
    private readonly LeaseHeartbeat _lease = new(gateway, info);
    private readonly SemaphoreSlim _network = new(Math.Clamp(concurrency, 1, 4));
    private readonly MediaPreviewCache _blocks = new(32 * 1024 * 1024);
    private readonly MediaPreviewCache _probes = new(1024 * 1024);
    public RemoteMediaInfo Info { get; } = info;
    public int Concurrency { get; } = Math.Clamp(concurrency, 1, 4);
    public Task<byte[]> ReadBlockAsync(long block, CancellationToken token) => _blocks.GetAsync(block.ToString(System.Globalization.CultureInfo.InvariantCulture), ct =>
    {
        var offset = checked(block * MediaWire.BlockSize);
        if (block < 0) throw new ArgumentOutOfRangeException(nameof(block));
        var count = (int)Math.Min(MediaWire.BlockSize, Info.Length - offset);
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(block));
        return ReadSourceAsync(offset, count, ct);
    }, token);

    private async Task<byte[]> ReadSourceAsync(long offset, int count, CancellationToken ct)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await _network.WaitAsync(ct).ConfigureAwait(false);
        var queueMs = timer.ElapsedMilliseconds;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var source = await gateway.OpenMediaRangeAsync(id, video, Info, offset, count, ct).ConfigureAwait(false);
                    var bytes = new byte[count];
                    await source.ReadExactlyAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
                    AppLogger.Info($"Media block received; bytes={count}; queueMs={queueMs}; transferMs={timer.ElapsedMilliseconds - queueMs}; attempt={attempt}");
                    return bytes;
                }
                catch (Exception exception) when (attempt < 1 && !ct.IsCancellationRequested && exception is HttpRequestException or IOException)
                { await Task.Delay(150, ct).ConfigureAwait(false); }
            }
        }
        finally { _network.Release(); }
    }

    public async Task CopyRangeAsync(Stream output, long offset, long length, CancellationToken token)
    {
        if (offset < 0 || length < 0 || offset > Info.Length - length) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length == 0) return;
        if (length <= 64 * 1024)
        {
            // Player format probes should not amplify a byte-sized request into a full media block.
            var bytes = await _probes.GetAsync(FormattableString.Invariant($"{offset}:{length}"),
                ct => ReadSourceAsync(offset, (int)length, ct), token).ConfigureAwait(false);
            await output.WriteAsync(bytes, token).ConfigureAwait(false);
            return;
        }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = new Dictionary<long, Task<byte[]>>();
        var next = offset / MediaWire.BlockSize;
        var last = (offset + length - 1) / MediaWire.BlockSize;
        void FillWindow()
        {
            while (pending.Count < Concurrency && next <= last)
            {
                pending.Add(next, ReadBlockAsync(next, request.Token));
                next++;
            }
        }
        try
        {
            FillWindow();
            while (length > 0)
            {
                var blockId = offset / MediaWire.BlockSize;
                var bytes = await pending[blockId].ConfigureAwait(false);
                pending.Remove(blockId);
                var start = (int)(offset % MediaWire.BlockSize);
                var count = (int)Math.Min(length, bytes.Length - start);
                FillWindow();
                await output.WriteAsync(bytes.AsMemory(start, count), token).ConfigureAwait(false);
                offset += count; length -= count;
            }
        }
        finally
        {
            request.Cancel();
            try { await Task.WhenAll(pending.Values).ConfigureAwait(false); } catch (Exception) { }
        }
    }
    public async Task DownloadAsync(string destination, CancellationToken token)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                file.SetLength(Info.Length);
                await Parallel.ForEachAsync(Blocks(), new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = token }, async (block, ct) =>
                {
                    var bytes = await ReadBlockAsync(block, ct).ConfigureAwait(false);
                    await RandomAccess.WriteAsync(file.SafeFileHandle, bytes, block * MediaWire.BlockSize, ct).ConfigureAwait(false);
                }).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private IEnumerable<long> Blocks()
    { for (long i = 0; i < Info.Length / MediaWire.BlockSize + (Info.Length % MediaWire.BlockSize == 0 ? 0 : 1); i++) yield return i; }
    public void Clear() { _blocks.Clear(); _probes.Clear(); }
    public async ValueTask DisposeAsync() { Clear(); await _lease.DisposeAsync().ConfigureAwait(false); }

    private sealed class LeaseHeartbeat : IAsyncDisposable
    {
        private readonly IRangeMediaGateway _gateway;
        private readonly RemoteMediaInfo _info;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public LeaseHeartbeat(IRangeMediaGateway gateway, RemoteMediaInfo info)
        { _gateway = gateway; _info = info; _loop = info.ETag.StartsWith("\"lease-", StringComparison.Ordinal) ? RunAsync() : Task.CompletedTask; }
        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token).ConfigureAwait(false);
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(10));
                        await _gateway.ManageMediaLeaseAsync(_info.ETag, false, deadline.Token).ConfigureAwait(false);
                    }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); await _loop.ConfigureAwait(false);
            if (_info.ETag.StartsWith("\"lease-", StringComparison.Ordinal))
            {
                try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await _gateway.ManageMediaLeaseAsync(_info.ETag, true, deadline.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { }
            }
            _stop.Dispose();
        }
    }
}
