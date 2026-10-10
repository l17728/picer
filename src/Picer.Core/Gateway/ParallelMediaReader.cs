using Picer.Core.Logging;

namespace Picer.Core.Gateway;

/// <summary>Identity-bound blocks shared by seeks, prefetch and download; reconnects never mix source versions.</summary>
public sealed class ParallelMediaReader(IRangeMediaGateway gateway, long id, bool video, RemoteMediaInfo info, int concurrency = 2) : IAsyncDisposable
{
    private readonly LeaseHeartbeat _lease = new(gateway, info);
    private readonly SemaphoreSlim _network = new(Math.Clamp(concurrency, 1, 4));
    private readonly MediaPreviewCache _blocks = new(32 * 1024 * 1024);
    public RemoteMediaInfo Info { get; } = info;
    public int Concurrency { get; } = Math.Clamp(concurrency, 1, 4);
    public Task<byte[]> ReadBlockAsync(long block, CancellationToken token) => _blocks.GetAsync(block.ToString(System.Globalization.CultureInfo.InvariantCulture), async ct =>
    {
        var offset = checked(block * MediaWire.BlockSize);
        var count = (int)Math.Min(MediaWire.BlockSize, Info.Length - offset);
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(block));
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
    }, token);

    public async Task CopyRangeAsync(Stream output, long offset, long length, CancellationToken token)
    {
        if (offset < 0 || length < 0 || offset > Info.Length - length) throw new ArgumentOutOfRangeException(nameof(offset));
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<byte[]>? pending = null;
        try
        {
            while (length > 0)
            {
                var blockId = offset / MediaWire.BlockSize;
                var bytes = await (pending ?? ReadBlockAsync(blockId, request.Token)).ConfigureAwait(false);
                var start = (int)(offset % MediaWire.BlockSize);
                var count = (int)Math.Min(length, bytes.Length - start);
                pending = length > count ? ReadBlockAsync(blockId + 1, request.Token) : null;
                await output.WriteAsync(bytes.AsMemory(start, count), token).ConfigureAwait(false);
                offset += count; length -= count;
            }
        }
        finally
        {
            request.Cancel();
            if (pending is not null) { try { await pending.ConfigureAwait(false); } catch (Exception) { } }
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
    public void Clear() => _blocks.Clear();
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
