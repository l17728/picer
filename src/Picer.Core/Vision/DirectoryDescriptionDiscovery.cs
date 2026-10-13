using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.Core.Vision;

public sealed record DirectoryDescriptionItem(ImageRecord? Photo, VideoRecord? Video, bool Described)
{
    public bool IsVideo => Video is not null;
    public long Id => Video?.Id ?? Photo!.Id;
    public string Path => Video?.Path ?? Photo!.Path;
}

public static class DirectoryDescriptionDiscovery
{
    public static async IAsyncEnumerable<DirectoryDescriptionItem> DiscoverAsync(PicerDatabase database, string directory,
        IReadOnlySet<string>? retryPaths = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<DirectoryDescriptionItem>(new BoundedChannelOptions(8)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        async Task ProduceAsync(bool video)
        {
            using var connection = database.OpenIndependentConnection();
            var through = connection.GetDescriptionHighWatermark(video);
            var path = ""; long id = 0;
            while (true)
            {
                var page = connection.ReadDescriptionPage(directory, video, path, id, through, token: stop.Token);
                if (page.Count == 0) return;
                // No reader or database transaction is held while applying backpressure.
                foreach (var item in page)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (retryPaths is null || retryPaths.Contains(item.Path)) await channel.Writer.WriteAsync(item, stop.Token).ConfigureAwait(false);
                }
                path = page[^1].Path.Replace('\\', '/'); id = page[^1].Id;
            }
        }
        async Task GuardedProducerAsync(bool video)
        {
            try { await ProduceAsync(video).ConfigureAwait(false); }
            catch (Exception exception) { channel.Writer.TryComplete(exception); await stop.CancelAsync().ConfigureAwait(false); throw; }
        }
        var producers = Task.WhenAll(Task.Run(() => GuardedProducerAsync(false), stop.Token), Task.Run(() => GuardedProducerAsync(true), stop.Token));
        async Task CompleteAsync()
        {
            try { await producers.ConfigureAwait(false); channel.Writer.TryComplete(); }
            catch (Exception exception) { channel.Writer.TryComplete(exception); }
        }
        var completion = CompleteAsync();
        try
        {
            // Internal cancellation stops sibling producers; preserve the original
            // producer error delivered by the channel to the consumer.
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
        }
        finally { await stop.CancelAsync().ConfigureAwait(false); await completion.ConfigureAwait(false); }
    }
}
