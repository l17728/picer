using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class DirectoryDescriptionStreamingTests
{
    [Fact]
    public async Task DiscoveryPreservesProducerFailureInsteadOfReportingUserCancellation()
    {
        using var database = new PicerDatabase(":memory:");
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var unused in DirectoryDescriptionDiscovery.DiscoverAsync(database, @"C:\Photos")) { }
        });
    }

    [Theory]
    [InlineData(@"C:\照片%_!\旅途", "c:/照片%_!/旅途/")]
    [InlineData(@"\\server\media\照片", "//server/media/照片/")]
    [InlineData(@"C:\", "C:/")]
    public void KeysetPaginationPreservesSubtreeCaseTiesAndHighWatermark(string directory, string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-description-pages-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var database = new PicerDatabase(Path.Combine(root, "picer.db"));
            var source = database.AddSource(directory);
            var paths = new[] { prefix + "A.jpg", prefix + "a.jpg", prefix + "b.jpg", prefix + "sub/one.jpg", prefix[..^1] + "other/outside.jpg" };
            for (var index = 0; index < paths.Length; index++)
                database.UpsertMirroredImage(new ImageRecord { Id = index + 1, SourceId = source.Id,
                    Path = paths[index], FileSize = 1, ModifiedUtc = DateTime.UtcNow });
            var through = database.GetDescriptionHighWatermark(false);
            database.UpsertMirroredImage(new ImageRecord { Id = 6, SourceId = source.Id, Path = prefix + "new.jpg", FileSize = 1, ModifiedUtc = DateTime.UtcNow });
            var ids = new List<long>(); var after = ""; long afterId = 0;
            while (true)
            {
                var page = database.ReadDescriptionPage(directory, false, after, afterId, through, pageSize: 1);
                if (page.Count == 0) break;
                var item = Assert.Single(page); ids.Add(item.Id);
                after = item.Path.Replace('\\', '/'); afterId = item.Id;
                Assert.True(ids.Count <= 4, "Keyset must advance through equal case-folded paths without repeating.");
            }
            Assert.Equal(new long[] { 1, 2, 3, 4 }, ids);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StreamingReadsOnlySelectedSubtreeAndCanStopWithProducersBlocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-description-stream-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var database = new PicerDatabase(Path.Combine(root, "picer.db"));
            var source = database.AddSource(@"C:\Photos");
            for (var id = 1; id <= 70; id++)
                database.UpsertMirroredImage(new ImageRecord { Id = id, SourceId = source.Id,
                    Path = $@"C:\Photos\set_1\sub\{id:D3}.jpg", FileSize = 1, ModifiedUtc = DateTime.UtcNow });
            database.UpsertMirroredImage(new ImageRecord { Id = 71, SourceId = source.Id, Path = @"C:\Photos\setX1\wrong.jpg", FileSize = 1, ModifiedUtc = DateTime.UtcNow });
            new VideoRepository(database).UpsertRemote(new(90, source.Id, @"C:\Photos\set_1\clip.mp4", 1,
                DateTime.UtcNow, 1, 2, 2, null, null, "active"), source.Id, true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var found = new List<DirectoryDescriptionItem>();
            await foreach (var item in DirectoryDescriptionDiscovery.DiscoverAsync(database, @"C:\Photos\set_1", cancellationToken: timeout.Token)) found.Add(item);
            Assert.Equal(71, found.Count);
            Assert.Equal(70, found.Count(item => !item.IsVideo));
            Assert.Single(found.Where(item => item.IsVideo));
            Assert.Equal(70, found.Where(item => !item.IsVideo).Select(item => item.Id).Distinct().Count());
            await using (var partial = DirectoryDescriptionDiscovery.DiscoverAsync(database, @"C:\Photos\set_1", cancellationToken: timeout.Token).GetAsyncEnumerator())
                Assert.True(await partial.MoveNextAsync());
            // Disposing the consumer cancels both bounded producers; no snapshot or reader remains open.
            database.UpsertMirroredImage(new ImageRecord { Id = 72, SourceId = source.Id, Path = @"C:\Photos\set_1\new.jpg", FileSize = 1, ModifiedUtc = DateTime.UtcNow });
            Assert.NotNull(database.GetImage(72));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
