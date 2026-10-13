using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.Core.Video;
using Picer.Core.Search;
using Picer.Core.People;
using Picer.Core.Gateway;
using Picer.IndexService.Identity;
using Xunit;

namespace Picer.Tests;

public sealed class SpaceSyncReceiverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-space-sync-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    public SpaceSyncReceiverTests() => _database = new(Path.Combine(_root, "picer.db"));
    private static AuthorizedDevice Device() => new(Guid.NewGuid().ToString("N"), "account", "space", Guid.NewGuid().ToString("N"), "public", "gui", AccountStore.EditorScopes, 1, 1);
    private (SpaceSyncReceiver Receiver, SpacePublisher Publisher) Publisher(string library)
    {
        var device = Device();
        var receiver = new SpaceSyncReceiver(_database, device);
        return (receiver, receiver.Register(new(library, device.DeviceId)));
    }
    private static SpaceSyncBatch Images(SpacePublisher publisher, long sequence = 1, string path = @"C:\Photos\same.jpg") =>
        new(publisher.PublisherId, publisher.Epoch, sequence, Guid.NewGuid().ToString("N"), "images",
            JsonSerializer.SerializeToElement(new SpaceImageBatch(1, @"C:\Photos", [new ImageRecord
            {
                Id = 1, SourceId = 1, Path = path, FileSize = 100, Sha256 = new string('a', 64), ContentSha256 = new string('a', 64),
                ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow
            }]), DeviceAuthorization.Json), [new("image", 1, Guid.NewGuid().ToString("N"))]);

    [Fact]
    public void DifferentMachinesWithSameLocalIdsAndPathsKeepBothReferences()
    {
        var library = Guid.NewGuid().ToString("N");
        var first = Publisher(library);
        var second = Publisher(library); // A cloned library on another device gets its own incarnation.
        Assert.NotEqual(first.Publisher.PublisherId, second.Publisher.PublisherId);
        Assert.Equal(200, first.Receiver.Apply(Images(first.Publisher)).Status);
        Assert.Equal(200, second.Receiver.Apply(Images(second.Publisher)).Status);
        var sources = _database.GetSources();
        Assert.Equal(2, sources.Count);
        var images = sources.SelectMany(source => _database.GetImages(source.Id)).ToArray();
        Assert.Equal(2, images.Length);
        Assert.NotEqual(images[0].Id, images[1].Id);
        Assert.NotEqual(images[0].Path, images[1].Path);
        Assert.All(images, image =>
        {
            Assert.True(PublishedMediaPath.TryUnqualify(image.Path, out _, out var original));
            Assert.Equal(@"C:\Photos\same.jpg", original);
        });
        using var contents = _database.Command("SELECT COUNT(*) FROM image_contents");
        Assert.Equal(1L, contents.ExecuteScalar());
    }

    [Fact]
    public void RetriedOperationReturnsOriginalReceiptAndChangedReplayCannotOverwrite()
    {
        var publisher = Publisher(Guid.NewGuid().ToString("N"));
        var batch = Images(publisher.Publisher);
        var first = publisher.Receiver.Apply(batch);
        Assert.Equal(first, publisher.Receiver.Apply(batch));
        Assert.Throws<InvalidOperationException>(() => publisher.Receiver.Apply(batch with { OperationId = Guid.NewGuid().ToString("N") }));
        Assert.Single(_database.GetImages(Assert.Single(_database.GetSources()).Id));
    }

    [Fact]
    public void InvalidPayloadRollsBackMappingsAndBusinessRowsButConsumesSequence()
    {
        var publisher = Publisher(Guid.NewGuid().ToString("N"));
        var bad = Images(publisher.Publisher, path: @"C:\Other\secret.jpg");
        Assert.Equal(422, publisher.Receiver.Apply(bad).Status);
        Assert.Empty(_database.GetSources());
        using var identities = _database.Command("SELECT COUNT(*) FROM sync_entity_identities");
        Assert.Equal(0L, identities.ExecuteScalar());
        var good = Images(publisher.Publisher, 2);
        Assert.Equal(200, publisher.Receiver.Apply(good).Status);
        Assert.Single(_database.GetSources());
    }

    [Fact]
    public void PhotoAndVideoPublishersCannotOverwriteEachOthersNumericIds()
    {
        var first = Publisher(Guid.NewGuid().ToString("N"));
        var second = Publisher(Guid.NewGuid().ToString("N"));
        foreach (var entry in new[] { first, second })
        {
            var payload = new SpaceVideoBatch(1, @"C:\Photos", [new VideoRecord(1, 1, @"C:\Photos\same.mp4", 200,
                DateTime.UtcNow, 30, 1920, 1080, null, null, "active") { Sha256 = new string('b', 64) }]);
            var batch = new SpaceSyncBatch(entry.Publisher.PublisherId, entry.Publisher.Epoch, 1, Guid.NewGuid().ToString("N"), "videos",
                JsonSerializer.SerializeToElement(payload, DeviceAuthorization.Json), [new("video", 1, Guid.NewGuid().ToString("N"))]);
            Assert.Equal(200, entry.Receiver.Apply(batch).Status);
        }
        var videos = new VideoRepository(_database).GetAll();
        Assert.Equal(2, videos.Count);
        Assert.Equal(2, videos.Select(video => video.Id).Distinct().Count());
        using var content = _database.Command("SELECT COUNT(*) FROM video_contents");
        Assert.Equal(1L, content.ExecuteScalar());
    }

    [Fact]
    public void CatalogAndFaceRelationsAreRemappedAndSharedTagConverges()
    {
        var first = Publisher(Guid.NewGuid().ToString("N"));
        var second = Publisher(Guid.NewGuid().ToString("N"));
        foreach (var entry in new[] { first, second })
        {
            var image = Images(entry.Publisher);
            Assert.Equal(200, entry.Receiver.Apply(image).Status);
            var catalog = new SpaceCatalogBatch([new Tag { Id = 1, Name = "旅行", CreatedUtc = DateTime.UtcNow }],
                [new Album { Id = 1, Name = "相册", CreatedUtc = DateTime.UtcNow }], [new PersonSummary(1, "家人", 0, 0)]);
            var catalogBatch = new SpaceSyncBatch(entry.Publisher.PublisherId, entry.Publisher.Epoch, 2, Guid.NewGuid().ToString("N"), "catalog",
                JsonSerializer.SerializeToElement(catalog, DeviceAuthorization.Json), [new("tag", 1, Guid.NewGuid().ToString("N")),
                    new("album", 1, Guid.NewGuid().ToString("N")), new("person", 1, Guid.NewGuid().ToString("N"))]);
            Assert.Equal(200, entry.Receiver.Apply(catalogBatch).Status);
            var now = DateTime.UtcNow.ToString("O");
            var metadata = new GatewayMetadata([new(1, 1)], [new(1, 1)], [],
                [new(1, 1, "model-v1", 0, 0, 0, 10, 10, 0.99, null, null, now)],
                [new(1, 1, "confirmed", 0.99, "user", now)], [], [], []);
            var metaBatch = new SpaceSyncBatch(entry.Publisher.PublisherId, entry.Publisher.Epoch, 3, Guid.NewGuid().ToString("N"), "metadata",
                JsonSerializer.SerializeToElement(new SpaceMetadataBatch(metadata), DeviceAuthorization.Json), [new("face", 1, Guid.NewGuid().ToString("N"))]);
            Assert.Equal(200, entry.Receiver.Apply(metaBatch).Status);
        }
        Assert.Single(SemanticEngine.GetTags(_database));
        Assert.Equal(2, SemanticEngine.GetAlbums(_database).Count);
        Assert.Equal(2, new PeopleService(_database).GetAll().Count);
        Assert.Equal(2, _database.GetAllMirroredFaceAssignments().Count);
        Assert.All(_database.GetSources().SelectMany(source => _database.GetImages(source.Id)), image =>
            Assert.Single(SemanticEngine.GetImageTags(_database, image.Id)));
    }

    [Fact]
    public void EpochChangeRejectsOldPublisherBeforeWriting()
    {
        var publisher = Publisher(Guid.NewGuid().ToString("N"));
        var batch = Images(publisher.Publisher);
        using (var epoch = _database.Command("UPDATE sync_space_state SET epoch='replacement' WHERE id=1")) epoch.ExecuteNonQuery();
        Assert.Throws<InvalidOperationException>(() => publisher.Receiver.Apply(batch));
        Assert.Empty(_database.GetSources());
    }

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
