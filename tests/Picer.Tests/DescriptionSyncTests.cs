using System.Net;
using System.Net.Http.Json;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class DescriptionSyncTests
{
    private static readonly GatewayMetadata Empty = new([], [], [], [], [], [], [], [], CloudVisionAnalyses: []);
    private static readonly Analyzer Model = new();
    private static CloudVisionResult Result(string text) => new(text, [], [], [], [], null, "none");

    private static string Epoch(PicerDatabase db)
    {
        using var command = db.Command("SELECT epoch FROM mirror_revision WHERE id=1");
        return (string)command.ExecuteScalar()!;
    }

    private static ImageRecord SeedPhoto(PicerDatabase db, string? epoch = null)
    {
        var source = db.AddSource("fixture");
        var image = db.UpsertImage(new ImageRecord { SourceId = source.Id, Path = "fixture.jpg", FileSize = 3,
            ModifiedUtc = DateTime.UnixEpoch, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active });
        db.SaveMirrorCheckpoint(new(epoch ?? Epoch(db), 0, 0));
        using var owned = db.Command("INSERT INTO mirror_owned_media VALUES('image',$id,$epoch)");
        owned.Parameters.AddWithValue("$id", image.Id); owned.Parameters.AddWithValue("$epoch", epoch ?? Epoch(db)); owned.ExecuteNonQuery();
        return image;
    }

    private static void Save(PicerDatabase db, ImageRecord image, string text = "fixture description")
    {
        db.EnableDescriptionSync = true;
        new CloudVisionRepository(db).Save(image, Model, "", "directory-requested", Result(text));
    }

    [Fact]
    public void DescriptionUploadRequiresIndexWritePermission()
    {
        Assert.Equal("index.write", Picer.IndexService.Identity.SpaceRoutePermissions.RequiredScope("POST", "/api/sync/v2/descriptions"));
        Assert.Null(Picer.IndexService.Identity.SpaceRoutePermissions.RequiredScope("GET", "/api/sync/v2/descriptions"));
    }

    [Fact]
    public void StartupRecoveryQueuesExistingLocalResultsWithoutReplacingPendingOperations()
    {
        using var db = new PicerDatabase(":memory:"); var image = SeedPhoto(db);
        new CloudVisionRepository(db).Save(image, Model, "", "directory-requested", Result("existing"));
        Assert.Empty(db.GetDescriptionOutbox());
        db.EnableDescriptionSync = true;
        db.RecoverUnsyncedDescriptions();
        var first = Assert.Single(db.GetDescriptionOutbox());
        db.RecoverUnsyncedDescriptions();
        Assert.Equal(first.OperationId, Assert.Single(db.GetDescriptionOutbox()).OperationId);
        db.AcknowledgeDescription(first.OperationId);
        db.ReconcileMirroredPhotoMetadata(Empty with { CloudVisionAnalyses = [first.Photo!] });
        db.RecoverUnsyncedDescriptions();
        Assert.Empty(db.GetDescriptionOutbox());
    }

    [Fact]
    public void ChangedIdentityDoesNotRestorePendingResultIntoAnotherContent()
    {
        using var db = new PicerDatabase(":memory:"); var image = SeedPhoto(db); Save(db, image);
        using (var change = db.Command("UPDATE images SET file_size=file_size+1 WHERE id=$id"))
        { change.Parameters.AddWithValue("$id", image.Id); change.ExecuteNonQuery(); }
        db.ReconcileMirroredPhotoMetadata(Empty, true);
        Assert.Empty(db.GetAllMirroredCloudVisionAnalyses());
        // Evidence stays in the outbox for explicit reconciliation; it is not applied to changed media.
        Assert.Single(db.GetDescriptionOutbox());
    }

    [Fact]
    public void SnapshotCannotDeleteUnconfirmedOrAcknowledgedButNotYetVisiblePhoto()
    {
        using var db = new PicerDatabase(":memory:");
        var image = SeedPhoto(db); Save(db, image);
        var item = Assert.Single(db.GetDescriptionOutbox());
        db.ReconcileMirroredPhotoMetadata(Empty, true);
        Assert.True(new CloudVisionRepository(db).HasCurrentDescription(image));
        db.AcknowledgeDescription(item.OperationId);
        db.ReconcileMirroredPhotoMetadata(Empty, true);
        Assert.Single(db.GetDescriptionOutbox());
        Assert.True(new CloudVisionRepository(db).HasCurrentDescription(image));
        db.ReconcileMirroredPhotoMetadata(Empty with { CloudVisionAnalyses = [item.Photo!] }, true);
        Assert.Empty(db.GetDescriptionOutbox());
        // A subsequent authoritative removal is still respected after confirmation.
        db.ReconcileMirroredPhotoMetadata(Empty, true);
        Assert.False(new CloudVisionRepository(db).HasCurrentDescription(image));
    }

    [Fact]
    public void LateAcknowledgmentDoesNotClearNewerResult()
    {
        using var db = new PicerDatabase(":memory:"); var image = SeedPhoto(db);
        Save(db, image, "first"); var first = Assert.Single(db.GetDescriptionOutbox());
        Save(db, image, "second"); var second = db.GetDescriptionOutbox().Single(x => x.OperationId != first.OperationId);
        db.AcknowledgeDescription(first.OperationId);
        db.ReconcileMirroredPhotoMetadata(Empty with { CloudVisionAnalyses = [first.Photo!] }, true);
        db.UpsertMirroredVideoMetadata(Empty with { CloudVisionAnalyses = [first.Photo!] });
        Assert.Equal(second.OperationId, Assert.Single(db.GetDescriptionOutbox()).OperationId);
        Assert.Equal(1, db.GetDescriptionSyncCounts().Pending);
        Assert.Equal("second", new CloudVisionRepository(db).GetLatestCompleted(image.Id)!.Value.Result.Summary);
    }

    [Fact]
    public void MissingBaselineRollsBackResultAndOutboxTogether()
    {
        using var db = new PicerDatabase(":memory:");
        var source = db.AddSource("fixture");
        var image = db.UpsertImage(new ImageRecord { SourceId = source.Id, Path = "fixture.jpg", FileSize = 3, ModifiedUtc = DateTime.UnixEpoch });
        Assert.Throws<InvalidOperationException>(() => Save(db, image));
        Assert.Empty(db.GetDescriptionOutbox());
        Assert.Empty(db.GetAllMirroredCloudVisionAnalyses());
    }

    [Fact]
    public void ServerAcceptanceIsDurableIdempotentAndFencedByMediaAndEpoch()
    {
        using var local = new PicerDatabase(":memory:"); using var server = new PicerDatabase(":memory:");
        SeedPhoto(server); var image = SeedPhoto(local, Epoch(server)); Save(local, image);
        var item = Assert.Single(local.GetDescriptionOutbox());
        Assert.Equal(item.OperationId, server.AcceptDescriptionUpload(item).OperationId);
        Assert.Equal(item.OperationId, server.AcceptDescriptionUpload(item).OperationId);
        Assert.Single(server.GetAllMirroredCloudVisionAnalyses());
        Assert.Throws<InvalidOperationException>(() => server.AcceptDescriptionUpload(item with { Epoch = Guid.NewGuid().ToString("N") }));
        Assert.Throws<InvalidOperationException>(() => server.AcceptDescriptionUpload(item with { Photo = item.Photo! with { Summary = "changed" } }));
        Assert.Throws<InvalidOperationException>(() => server.AcceptDescriptionUpload(item with { OperationId = Guid.NewGuid().ToString("N"), Sha256 = new string('a', 64) }));
    }

    [Fact]
    public async Task FailedTransportKeepsDurableResultAndRetryDoesNotNeedModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "description-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var server = new PicerDatabase(":memory:"); SeedPhoto(server);
            using var local = new PicerDatabase(Path.Combine(root, "mirror.db"));
            var image = SeedPhoto(local, Epoch(server)); Save(local, image);
            var calls = 0;
            using var handler = new Handler(async request =>
            {
                Assert.EndsWith("/api/sync/v2/descriptions", request.RequestUri!.AbsolutePath);
                Assert.Equal(Epoch(server), Assert.Single(request.Headers.GetValues("X-Picer-Mirror-Epoch")));
                var item = (await request.Content!.ReadFromJsonAsync<DescriptionUpload>())!;
                var receipt = server.AcceptDescriptionUpload(item);
                // The service committed, but the first response was lost.
                if (++calls == 1) throw new HttpRequestException("fixture disconnect");
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(receipt) };
            });
            using var client = new IndexSyncClient(new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") }, accountMode: true);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.PushDescriptionsAsync(local));
            local.ReconcileMirroredPhotoMetadata(Empty, true);
            Assert.Equal(1, local.GetDescriptionSyncCounts().Pending);
            await client.PushDescriptionsAsync(local);
            Assert.Equal((0L, 1L), local.GetDescriptionSyncCounts());
            local.ReconcileMirroredPhotoMetadata(Empty with { CloudVisionAnalyses = server.GetAllMirroredCloudVisionAnalyses() }, true);
            Assert.Empty(local.GetDescriptionOutbox());
            Assert.Single(server.GetAllMirroredCloudVisionAnalyses());
            Assert.True(new CloudVisionRepository(local).HasCurrentDescription(image));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DescriptionOnlyPublicationReportsChangeAndDoesNotUploadConfirmedResultAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), "description-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var local = new PicerDatabase(Path.Combine(root, "mirror.db"));
            var image = SeedPhoto(local); Save(local, image); var uploads = 0;
            using var handler = new Handler(async request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/descriptions", StringComparison.Ordinal))
                {
                    uploads++;
                    var item = (await request.Content!.ReadFromJsonAsync<DescriptionUpload>())!;
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DescriptionUploadReceipt(item.OperationId)) };
                }
                Assert.EndsWith("/metadata-edits", request.RequestUri!.AbsolutePath);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<MetadataEdit>()) };
            });
            using var client = new IndexSyncClient(new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") }, accountMode: true);
            var checkpoint = Path.Combine(root, "checkpoint.json");
            Assert.True((await client.PushIncrementalAsync(local, checkpoint)).Changed);
            Assert.False((await client.PushIncrementalAsync(local, checkpoint)).Changed);
            Assert.Equal(1, uploads);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConflictDoesNotBlockUnrelatedPendingDescription()
    {
        var root = Path.Combine(Path.GetTempPath(), "description-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var local = new PicerDatabase(Path.Combine(root, "mirror.db"));
            var first = SeedPhoto(local); Save(local, first);
            var second = local.UpsertImage(new ImageRecord { SourceId = first.SourceId, Path = "second.jpg", FileSize = 3, ModifiedUtc = DateTime.UnixEpoch });
            Save(local, second);
            using var handler = new Handler(async request =>
            {
                var item = (await request.Content!.ReadFromJsonAsync<DescriptionUpload>())!;
                return item.MediaId == first.Id ? new(HttpStatusCode.Conflict)
                    : new(HttpStatusCode.OK) { Content = JsonContent.Create(new DescriptionUploadReceipt(item.OperationId)) };
            });
            using var client = new IndexSyncClient(new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") }, accountMode: true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.PushDescriptionsAsync(local));
            Assert.Equal((1L, 1L), local.GetDescriptionSyncCounts());
            Assert.Equal(first.Id, Assert.Single(local.GetDescriptionOutbox(pendingOnly: true)).MediaId);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public void SharedVideoReferenceWithDifferentTimestampKeepsPendingSummary()
    {
        using var db = new PicerDatabase(":memory:");
        var source = db.AddSource("fixture"); db.SaveMirrorCheckpoint(new(Epoch(db), 0, 0)); db.EnableDescriptionSync = true;
        var videos = new VideoRepository(db);
        var first = new VideoRecord(12, source.Id, "first.mp4", 3, DateTime.UnixEpoch, 1, 320, 180, null, null, "active") { Sha256 = new string('a', 64) };
        videos.UpsertMirrored(first, source.Id);
        videos.UpsertMirrored(first with { Id = 13, Path = "copy.mp4", ModifiedUtc = DateTime.UnixEpoch.AddDays(1) }, source.Id);
        Assert.Equal(videos.GetContentId(12), videos.GetContentId(13));
        videos.SaveSummary(13, Model, videos.Get(13)!, Result("shared video"));
        db.QueueRecoveredDescription(null, Assert.Single(db.GetAllMirroredVideoSummaries()) with { VideoId = 13 });
        Assert.Equal(12, Assert.Single(db.GetDescriptionOutbox()).MediaId);
        db.UpsertMirroredVideoMetadata(Empty, authoritative: true);
        Assert.True(videos.HasCurrentSummary(videos.Get(12)!));
        Assert.True(videos.HasCurrentSummary(videos.Get(13)!));
    }

    [Fact]
    public void Version91UpgradeBacksUpDatabaseAndPreservesCompletedDescription()
    {
        var root = Path.Combine(Path.GetTempPath(), "description-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var path = Path.Combine(root, "mirror.db");
        try
        {
            using (var db = new PicerDatabase(path))
            {
                var image = SeedPhoto(db);
                new CloudVisionRepository(db).Save(image, Model, "", "directory-requested", Result("before migration"));
                using var downgrade = db.Command("DROP TRIGGER description_image_removed; DROP TRIGGER description_video_removed; DROP TABLE description_versions; DROP TABLE description_outbox; DROP TABLE description_receipts; DROP TABLE description_recovery_state; PRAGMA user_version=91;");
                downgrade.ExecuteNonQuery();
            }
            using (var upgraded = new PicerDatabase(path) { EnableDescriptionSync = true })
            {
                upgraded.RecoverUnsyncedDescriptions();
                Assert.Equal("before migration", Assert.Single(upgraded.GetDescriptionOutbox()).Photo!.Summary);
                Assert.Single(Directory.GetDirectories(path + ".migration-backups"));
                using var integrity = upgraded.Command("PRAGMA quick_check"); Assert.Equal("ok", integrity.ExecuteScalar());
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public void VideoResultSurvivesStaleSnapshotAndPreservesContentIdentity()
    {
        using var db = new PicerDatabase(":memory:");
        var source = db.AddSource("fixture"); db.SaveMirrorCheckpoint(new(Epoch(db), 0, 0)); db.EnableDescriptionSync = true;
        var videos = new VideoRepository(db);
        videos.UpsertMirrored(new VideoRecord(12, source.Id, "fixture.mp4", 3, DateTime.UnixEpoch,
            1, 320, 180, null, null, "active"), source.Id);
        var video = videos.Get(12)!;
        videos.SaveSummary(video.Id, Model, video, Result("video fixture"));
        var item = Assert.Single(db.GetDescriptionOutbox());
        Assert.Equal(video.Id, item.MediaId);
        db.UpsertMirroredVideoMetadata(Empty, authoritative: true);
        Assert.True(videos.HasCurrentSummary(video));
        db.AcknowledgeDescription(item.OperationId);
        db.UpsertMirroredVideoMetadata(Empty, authoritative: true);
        Assert.True(videos.HasCurrentSummary(video));
        db.UpsertMirroredVideoMetadata(Empty with { VideoSummaries = [item.Video!] }, authoritative: true);
        Assert.Empty(db.GetDescriptionOutbox());
        db.UpsertMirroredVideoMetadata(Empty, authoritative: true);
        Assert.False(videos.HasCurrentSummary(video));
    }

    [Fact]
    public async Task UnionKeepsBothPhotoVersionsAndConvergesEvenWhenOlderUploadArrivesLast()
    {
        using var local = new PicerDatabase(":memory:"); using var serverFiles = new FileDatabase();
        var server = serverFiles.Database;
        var remoteImage = SeedPhoto(server); var image = SeedPhoto(local, Epoch(server));
        new CloudVisionRepository(server).Save(remoteImage, Model, "", "directory-requested", Result("remote newer"));
        Save(local, image, "local older");
        var first = Assert.Single(local.GetDescriptionOutbox());
        var older = first.Photo! with { UpdatedUtc = DateTime.UtcNow.AddDays(-1).ToString("o") };
        // Recovered versions also enter the durable queue, rather than replacing each other.
        local.QueueRecoveredDescription(older, null);
        foreach (var item in local.GetDescriptionOutbox())
        {
            server.AcceptDescriptionUpload(item);
            local.AcknowledgeDescription(item.OperationId);
        }
        var snapshot = await new LocalGateway(server, new Picer.Core.Thumbnails.ThumbnailService(Path.GetTempPath())).GetMetadataAsync();
        local.ReconcileMirroredPhotoMetadata(snapshot, true);
        local.UpsertMirroredVideoMetadata(snapshot, true);
        Assert.Empty(local.GetDescriptionOutbox());
        Assert.Equal(3, server.GetDescriptionVersions().Count);
        Assert.Equal(server.GetDescriptionVersions().Select(x => x.VersionId), local.GetDescriptionVersions().Select(x => x.VersionId));
        Assert.Equal(server.GetAllMirroredCloudVisionAnalyses(), local.GetAllMirroredCloudVisionAnalyses());
        using (var localText = local.Command("SELECT content FROM media_text_documents WHERE media_id=$id"))
        using (var remoteText = server.Command("SELECT content FROM media_text_documents WHERE media_id=$id"))
        {
            localText.Parameters.AddWithValue("$id", image.Id); remoteText.Parameters.AddWithValue("$id", image.Id);
            Assert.Equal(remoteText.ExecuteScalar(), localText.ExecuteScalar());
            Assert.Contains(Assert.Single(server.GetAllMirroredCloudVisionAnalyses()).Summary!, (string)localText.ExecuteScalar()!);
        }
        // Missing snapshot entries are not tombstones under the union protocol.
        local.ReconcileMirroredPhotoMetadata(Empty with { DescriptionVersions = [] }, true);
        Assert.True(new CloudVisionRepository(local).HasCurrentDescription(image));
        using var purge = local.Command("DELETE FROM images WHERE id=$id");
        purge.Parameters.AddWithValue("$id", image.Id); purge.ExecuteNonQuery();
        Assert.Empty(local.GetDescriptionVersions());
        Assert.Empty(local.GetDescriptionOutbox());
    }

    [Fact]
    public void EqualTimestampConflictsChooseSameProjectionInEitherArrivalOrder()
    {
        using var first = new PicerDatabase(":memory:"); using var second = new PicerDatabase(":memory:");
        var image = SeedPhoto(first); SeedPhoto(second);
        Save(first, image, "alpha");
        var a = Assert.Single(first.GetDescriptionOutbox()).Photo!;
        var b = a with { Summary = "beta", ResultJson = System.Text.Json.JsonSerializer.Serialize(Result("beta")) };
        foreach (var candidate in new[] { a, b }) first.QueueRecoveredDescription(candidate, null);
        foreach (var candidate in new[] { b, a }) second.QueueRecoveredDescription(candidate, null);
        first.UpsertMirroredVideoMetadata(Empty with { DescriptionVersions = [] });
        second.UpsertMirroredVideoMetadata(Empty with { DescriptionVersions = [] });
        Assert.Equal(first.GetAllMirroredCloudVisionAnalyses(), second.GetAllMirroredCloudVisionAnalyses());
        Assert.Equal(2, first.GetDescriptionVersions().Count);
        Assert.Equal(first.GetDescriptionVersions().Select(x => x.VersionId), second.GetDescriptionVersions().Select(x => x.VersionId));
    }

    [Fact]
    public async Task UnionRetainsEveryVideoVersionAndGatewayPreservesTimestamp()
    {
        using var local = new PicerDatabase(":memory:"); using var serverFiles = new FileDatabase();
        var server = serverFiles.Database;
        foreach (var db in new[] { local, server })
        {
            var source = db.AddSource("fixture"); db.SaveMirrorCheckpoint(new(Epoch(server), 0, 0));
            new VideoRepository(db).UpsertMirrored(new VideoRecord(12, source.Id, "fixture.mp4", 3, DateTime.UnixEpoch,
                1, 320, 180, null, null, "active"), source.Id);
        }
        local.EnableDescriptionSync = true;
        var videos = new VideoRepository(local);
        videos.SaveSummary(12, Model, videos.Get(12)!, Result("first"));
        videos.SaveSummary(12, Model, videos.Get(12)!, Result("second"));
        Assert.Equal(2, local.GetDescriptionOutbox().Count);
        foreach (var item in local.GetDescriptionOutbox().Reverse())
        {
            server.AcceptDescriptionUpload(item); local.AcknowledgeDescription(item.OperationId);
        }
        var snapshot = await new LocalGateway(server, new Picer.Core.Thumbnails.ThumbnailService(Path.GetTempPath())).GetMetadataAsync();
        Assert.NotNull(Assert.Single(snapshot.VideoSummaries).UpdatedUtc);
        Assert.Equal(snapshot.VideoSummaries, Picer.IndexService.IndexApiHandlers.ReadMetadata(server).VideoSummaries);
        local.UpsertMirroredVideoMetadata(snapshot, true);
        Assert.Empty(local.GetDescriptionOutbox());
        Assert.Equal(2, server.GetDescriptionVersions().Count);
        Assert.Equal(server.GetDescriptionVersions().Select(x => x.VersionId), local.GetDescriptionVersions().Select(x => x.VersionId));
        Assert.Equal(server.GetAllMirroredVideoSummaries(), local.GetAllMirroredVideoSummaries());
    }

    [Fact]
    public void LocalResultsAndFailedRetryPreserveCompletedVersionHistory()
    {
        using var db = new PicerDatabase(":memory:"); var image = SeedPhoto(db);
        var repository = new CloudVisionRepository(db);
        repository.Save(image, Model, "", "directory-requested", Result("previous"));
        repository.MarkProcessing(image, Model, "", "directory-requested");
        repository.SaveFailure(image, Model, "", "directory-requested", "fixture_failure");
        Assert.Equal("previous", Assert.Single(db.GetDescriptionVersions()).Photo!.Summary);
        Assert.Empty(db.GetDescriptionOutbox());
        repository.Save(image, Model, "", "directory-requested", Result("new"));
        Assert.Equal(2, db.GetDescriptionVersions().Count);
    }

    [Fact]
    public void NewEpochDoesNotReuseOldDeliveryOperationForIdenticalResult()
    {
        using var db = new PicerDatabase(":memory:"); var image = SeedPhoto(db); Save(db, image);
        var first = Assert.Single(db.GetDescriptionOutbox());
        var epoch = Guid.NewGuid().ToString("N"); db.SaveMirrorCheckpoint(new(epoch, 0, 0));
        db.QueueRecoveredDescription(first.Photo, null);
        Assert.Equal(2, db.GetDescriptionOutbox().Count);
        Assert.Single(db.GetDescriptionOutbox().Where(x => x.Epoch == epoch));
        Assert.Single(db.GetDescriptionVersions());
    }

    private sealed class FileDatabase : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "description-union-" + Guid.NewGuid().ToString("N"));
        public PicerDatabase Database { get; }
        public FileDatabase() { Directory.CreateDirectory(_root); Database = new(Path.Combine(_root, "index.db")); }
        public void Dispose() { Database.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class Analyzer : ICloudVisionAnalyzer
    {
        public string Provider => "fixture";
        public string ModelVersion => "fixture-v1";
        public string PromptVersion => "fixture-v1";
        public Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("model_must_not_be_called_during_sync");
    }
}
