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
        Assert.Equal("index.write", Picer.IndexService.Identity.SpaceRoutePermissions.RequiredScope("POST", "/api/sync/v2/descriptions/batch"));
        Assert.Equal("index.read", Picer.IndexService.Identity.SpaceRoutePermissions.RequiredScope("POST", "/api/sync/v2/descriptions/changes"));
        Assert.Null(Picer.IndexService.Identity.SpaceRoutePermissions.RequiredScope("GET", "/api/sync/v2/descriptions"));
    }

    [Fact]
    public async Task AuthorizedHttpBatchAndDeltaPreservePartialSuccessAndRejectReadOnlyWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-description-http-" + Guid.NewGuid().ToString("N"));
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var origin = $"https://127.0.0.1:{port}";
        var instance = Picer.IndexService.Identity.AccountServiceHost.Build(root, origin, port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await Picer.IndexService.IndexServiceHost.StartAsync(instance.App);
            var account = instance.Accounts.Register("description-owner", "description-test-password-123!", "测试空间");
            var space = Assert.Single(instance.Accounts.ListSpaces(account));
            using var signing = Picer.IndexService.Identity.IdentitySigningKey.LoadOrCreate(instance.Accounts.Root, instance.Accounts.ServiceId);
            async Task<Picer.Core.Identity.DeviceAccountClient> Pair(bool editing)
            {
                var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
                var id = Guid.NewGuid().ToString("N");
                instance.Accounts.CreateEnrollment(new(id, Picer.Core.Identity.DeviceAuthorization.ExportPublicKey(key), "fixture", "gui"));
                var pin = Convert.ToBase64String(Picer.IndexService.ServiceSecurity.ComputeSpkiPin(instance.Certificate));
                var document = instance.Accounts.ApproveEnrollment(account, id, space.Id, origin, pin, signing, editing, true);
                var connection = new Picer.Core.Identity.DeviceConnection(origin, instance.Accounts.ServiceId, pin,
                    Picer.Core.Identity.DeviceAuthorization.ExportPublicKey(signing), "test", id, "fixture", "gui");
                var client = new Picer.Core.Identity.DeviceAccountClient(connection, key, value => connection = value);
                await client.ImportAsync(Picer.Core.Identity.DeviceAuthorization.WriteFileDocument(document), deadline.Token);
                return client;
            }
            using var writer = await Pair(true);
            using var reader = await Pair(false);
            DescriptionUpload upload;
            using (var database = new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id), "picer.db")))
            {
                var photo = SeedPhoto(database); Save(database, photo);
                upload = Assert.Single(database.GetDescriptionOutbox());
            }
            using (var health = await reader.SpaceClient.GetAsync("health", deadline.Token))
            {
                health.EnsureSuccessStatusCode();
                var data = await health.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(deadline.Token);
                Assert.Equal(1, data.GetProperty("descriptionDelta").GetInt32());
            }
            var batch = new DescriptionUploadBatch([upload with { OperationId = Guid.NewGuid().ToString("N"), Epoch = "stale" }, upload]);
            using (var denied = await reader.SpaceClient.PostAsJsonAsync("api/sync/v2/descriptions/batch", batch, deadline.Token))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var accepted = await writer.SpaceClient.PostAsJsonAsync("api/sync/v2/descriptions/batch", batch, deadline.Token))
            {
                accepted.EnsureSuccessStatusCode();
                var receipt = await accepted.Content.ReadFromJsonAsync<DescriptionBatchReceipt>(deadline.Token);
                Assert.Equal(new[] { "conflict", "accepted" }, receipt!.Items.Select(item => item.Status));
            }
            using var changes = await reader.SpaceClient.PostAsJsonAsync("api/sync/v2/descriptions/changes", new DescriptionDeltaRequest(null), deadline.Token);
            changes.EnsureSuccessStatusCode();
            var page = await changes.Content.ReadFromJsonAsync<DescriptionDeltaPage>(deadline.Token);
            Assert.NotEmpty(page!.Entries);
            using var invalid = await reader.SpaceClient.PostAsJsonAsync("api/sync/v2/descriptions/changes",
                new DescriptionDeltaRequest(page.Cursor with { Scope = "wrong-grant" }), deadline.Token);
            Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
            instance.Accounts.Revoke(account, reader.Connection.Session!.GrantId);
            using var revoked = await reader.SpaceClient.PostAsJsonAsync("api/sync/v2/descriptions/changes", new DescriptionDeltaRequest(null), deadline.Token);
            Assert.Contains(revoked.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        }
        finally
        {
            await instance.App.StopAsync(); await instance.App.DisposeAsync(); instance.Certificate.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DescriptionDeltaReadDoesNotAcquireWriterLock()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-delta-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var writer = new PicerDatabase(Path.Combine(root, "index.db"));
            var photo = SeedPhoto(writer); Save(writer, photo);
            using var reader = writer.OpenReadOnlyConnection();
            using var pending = writer.BeginTransaction();
            using var change = writer.Command("UPDATE images SET width=width+1 WHERE id=$id");
            change.Transaction = pending; change.Parameters.AddWithValue("$id", photo.Id); change.ExecuteNonQuery();
            Assert.Single(reader.ReadDescriptionDelta(new(null), "scope").Entries);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void BatchAcceptanceKeepsPartialSuccessAndRejectsOperationReuse()
    {
        using var db = new PicerDatabase(":memory:"); var image = SeedPhoto(db); Save(db, image);
        var item = Assert.Single(db.GetDescriptionOutbox());
        var bad = item with { OperationId = Guid.NewGuid().ToString("N"), Epoch = "stale" };
        var receipt = db.AcceptDescriptionBatch(new([bad, item]));
        Assert.Equal(new[] { "conflict", "accepted" }, receipt.Items.Select(x => x.Status));
        Assert.Equal("accepted", Assert.Single(db.AcceptDescriptionBatch(new([item])).Items).Status);
        var reused = item with { Photo = item.Photo! with { Summary = "different" } };
        Assert.Equal("conflict", Assert.Single(db.AcceptDescriptionBatch(new([reused])).Items).Status);
        Assert.Throws<ArgumentException>(() => db.AcceptDescriptionBatch(new([item, item])));
    }

    [Fact]
    public void DescriptionDeltaIsAtomicBoundedAndProtectsAgainstLateLegacySnapshot()
    {
        using var server = new PicerDatabase(":memory:"); var image = SeedPhoto(server);
        using var mirror = new PicerDatabase(":memory:"); SeedPhoto(mirror, Epoch(server));
        Save(server, image, "first");
        var page = server.ReadDescriptionDelta(new(null), "scope");
        Assert.Single(page.Entries);
        mirror.ApplyDescriptionDelta(page, null);
        var first = mirror.GetDescriptionDeltaCursor();
        Save(server, image, "second");
        var second = server.ReadDescriptionDelta(new(first), "scope");
        Assert.Single(second.Entries);
        Assert.Throws<InvalidDataException>(() => mirror.ApplyDescriptionDelta(second with
            { Entries = [second.Entries[0], second.Entries[0]] }, first));
        Assert.Equal(first, mirror.GetDescriptionDeltaCursor());
        Assert.Single(mirror.GetDescriptionVersions());
        mirror.ApplyDescriptionDelta(second, first);
        mirror.ReconcileMirroredPhotoMetadata(Empty, true);
        Assert.Equal(2, mirror.GetDescriptionVersions().Count);
        Assert.Equal("second", Assert.Single(mirror.GetAllMirroredCloudVisionAnalyses()).Summary);
        var same = server.ReadDescriptionDelta(new(second.Cursor), "scope");
        Assert.Empty(same.Entries);
        mirror.ApplyDescriptionDelta(same, second.Cursor);
        Assert.Throws<InvalidOperationException>(() => server.ReadDescriptionDelta(new(second.Cursor), "other-scope"));
        Assert.Throws<InvalidOperationException>(() => mirror.ApplyDescriptionDelta(second, first));
    }

    [Fact]
    public void DescriptionDeltaFreezesHighWaterAndPurgesDeletedPayloads()
    {
        using var db = new PicerDatabase(":memory:"); var image = SeedPhoto(db);
        for (var index = 0; index < 23; index++) Save(db, image, "private fixture " + index);
        var first = db.ReadDescriptionDelta(new(null), "scope");
        Assert.Equal(20, first.Entries.Count);
        Save(db, image, "later fixture");
        var second = db.ReadDescriptionDelta(new(first.Cursor, first.Through), "scope");
        Assert.Equal(3, second.Entries.Count);
        Assert.Equal(first.Through, second.Cursor.Sequence);
        Assert.Single(db.ReadDescriptionDelta(new(second.Cursor), "scope").Entries);
        using (var remove = db.Command("DELETE FROM images WHERE id=$id"))
        { remove.Parameters.AddWithValue("$id", image.Id); remove.ExecuteNonQuery(); }
        using var leaked = db.Command("SELECT COUNT(*) FROM description_changes WHERE payload LIKE '%fixture%'");
        Assert.Equal(0L, leaked.ExecuteScalar());
        Assert.All(db.ReadDescriptionDelta(new(null), "scope").Entries, item => Assert.True(item.Deleted));
    }

    [Fact]
    public void DeltaConfirmationBeforeLostUploadResponseStillCompletesAfterIdempotentRetry()
    {
        using var server = new PicerDatabase(":memory:"); SeedPhoto(server);
        using var mirror = new PicerDatabase(":memory:"); var image = SeedPhoto(mirror, Epoch(server)); Save(mirror, image);
        var upload = Assert.Single(mirror.GetDescriptionOutbox());
        server.AcceptDescriptionUpload(upload);
        mirror.ApplyDescriptionDelta(server.ReadDescriptionDelta(new(null), "scope"), null);
        Assert.Equal((1L, 0L), mirror.GetDescriptionSyncCounts());
        server.AcceptDescriptionUpload(upload);
        mirror.AcknowledgeDescription(upload.OperationId);
        Assert.Empty(mirror.GetDescriptionOutbox());
        mirror.ReconcileMirroredPhotoMetadata(Empty, true);
        Assert.True(new CloudVisionRepository(mirror).HasCurrentDescription(image));
    }

    [Fact]
    public void OutboxSequenceDoesNotReuseDeletedHighWater()
    {
        using var db = new PicerDatabase(":memory:"); var image = SeedPhoto(db); Save(db, image, "first");
        var through = db.DescriptionOutboxHighWater();
        using (var clear = db.Command("DELETE FROM description_outbox")) clear.ExecuteNonQuery();
        Save(db, image, "second");
        Assert.True(db.DescriptionOutboxHighWater() > through);
        Assert.Empty(db.ReadDescriptionOutboxPage(0, through));
        Assert.Single(db.ReadDescriptionOutboxPage(through, db.DescriptionOutboxHighWater()));
    }

    [Fact]
    public void ExplicitDeltaDeletionCannotBeResurrectedByAnOlderFullSnapshot()
    {
        using var server = new PicerDatabase(":memory:"); var image = SeedPhoto(server); Save(server, image);
        using var mirror = new PicerDatabase(":memory:"); SeedPhoto(mirror, Epoch(server));
        var first = server.ReadDescriptionDelta(new(null), "scope");
        mirror.ApplyDescriptionDelta(first, null);
        var old = Empty with { CloudVisionAnalyses = server.GetAllMirroredCloudVisionAnalyses(), DescriptionVersions = server.GetDescriptionVersions() };
        using (var delete = server.Command("DELETE FROM cloud_vision_analyses; DELETE FROM description_versions;")) delete.ExecuteNonQuery();
        mirror.ApplyDescriptionDelta(server.ReadDescriptionDelta(new(first.Cursor), "scope"), first.Cursor);
        mirror.UpsertMirroredVideoMetadata(old);
        Assert.Empty(mirror.GetDescriptionVersions());
        Assert.Empty(mirror.GetAllMirroredCloudVisionAnalyses());
    }

    [Fact]
    public void UnchangedSearchDocumentSkipsWritesButRepairsMissingFtsEntry()
    {
        using var db = new PicerDatabase(":memory:"); var image = SeedPhoto(db);
        var repository = new Picer.Core.Search.MediaTextIndexRepository(db);
        repository.UpsertImage(image, ["fixture"]);
        using var changes = db.Command("SELECT total_changes()"); var before = changes.ExecuteScalar();
        repository.UpsertImage(image, ["fixture"]);
        Assert.Equal(before, changes.ExecuteScalar());
        using (var remove = db.Command("DELETE FROM media_text_fts WHERE rowid=$id"))
        { remove.Parameters.AddWithValue("$id", image.Id); remove.ExecuteNonQuery(); }
        repository.UpsertImage(image, ["fixture"]);
        using var count = db.Command("SELECT COUNT(*) FROM media_text_fts"); Assert.Equal(1L, count.ExecuteScalar());
    }

    [Fact]
    public async Task BatchUploadUsesBoundedPagesAndDoesNotLoseConcurrentArrivals()
    {
        var root = Path.Combine(Path.GetTempPath(), "description-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var local = new PicerDatabase(Path.Combine(root, "mirror.db"));
            var image = SeedPhoto(local);
            for (var index = 0; index < 43; index++) Save(local, image, "version " + index);
            var calls = 0;
            using var handler = new Handler(async request =>
            {
                calls++;
                if (request.RequestUri!.AbsolutePath.EndsWith("/batch", StringComparison.Ordinal))
                {
                    Assert.True((await request.Content!.ReadAsByteArrayAsync()).Length <= DescriptionUploadBatch.MaxBytes);
                    var batch = (await request.Content.ReadFromJsonAsync<DescriptionUploadBatch>())!;
                    Assert.InRange(batch.Items.Count, 1, 20);
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DescriptionBatchReceipt(
                        batch.Items.Select(x => new DescriptionBatchItemReceipt(x.OperationId, "accepted")).ToArray())) };
                }
                var item = (await request.Content!.ReadFromJsonAsync<DescriptionUpload>())!;
                Save(local, image, "arrived during upload");
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new DescriptionUploadReceipt(item.OperationId)) };
                response.Headers.Add(DescriptionUploadBatch.CapabilityHeader, "1");
                return response;
            });
            using var client = new IndexSyncClient(new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") }, accountMode: true);
            await client.PushDescriptionsAsync(local);
            Assert.Equal(4, calls);
            Assert.Equal((1L, 43L), local.GetDescriptionSyncCounts());
            await client.PushDescriptionsAsync(local);
            Assert.Equal((0L, 44L), local.GetDescriptionSyncCounts());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
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
        // Omission, including a late legacy snapshot, is not an explicit deletion.
        db.ReconcileMirroredPhotoMetadata(Empty, true);
        Assert.True(new CloudVisionRepository(db).HasCurrentDescription(image));
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
        Assert.True(videos.HasCurrentSummary(video));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradedLegacyDescriptionsConvergeWithoutWritingDuringSnapshotRead(bool video)
    {
        using var files = new FileDatabase();
        var server = files.Database;
        using var local = new PicerDatabase(":memory:");
        if (video)
        {
            foreach (var db in new[] { server, local })
            {
                var source = db.AddSource("fixture");
                db.SaveMirrorCheckpoint(new(Epoch(server), 0, 0));
                new VideoRepository(db).UpsertMirrored(new VideoRecord(12, source.Id, "fixture.mp4", 3,
                    DateTime.UnixEpoch, 1, 320, 180, null, null, "active"), source.Id);
            }
            var repository = new VideoRepository(server);
            repository.SaveSummary(12, Model, repository.Get(12)!, Result("legacy video"));
        }
        else
        {
            var image = SeedPhoto(server);
            SeedPhoto(local, Epoch(server));
            Save(server, image, "legacy photo");
        }
        // A v91 upgrade has persisted current rows and newly empty history tables.
        using (var legacy = server.Command("DELETE FROM description_versions; DELETE FROM description_outbox"))
            legacy.ExecuteNonQuery();
        var snapshot = await new LocalGateway(server, new Picer.Core.Thumbnails.ThumbnailService(Path.GetTempPath())).GetMetadataAsync();
        Assert.Single(snapshot.DescriptionVersions!);
        using (var stored = server.Command("SELECT COUNT(*) FROM description_versions"))
            Assert.Equal(0L, stored.ExecuteScalar());
        local.UpsertMirroredVideoMetadata(snapshot, true);
        Assert.Equal(server.GetDescriptionVersions().Select(x => x.VersionId), local.GetDescriptionVersions().Select(x => x.VersionId));
        Assert.Empty(local.GetDescriptionOutbox());
        using (var changed = server.Command(video ? "UPDATE videos SET file_size=file_size+1" : "UPDATE images SET file_size=file_size+1"))
            changed.ExecuteNonQuery();
        Assert.Empty(server.GetDescriptionVersions());
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
