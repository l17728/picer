using System.Net;
using System.Net.Http.Json;
using Picer.Core.Data;
using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class AccountMirrorPublicationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "account-mirror-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;

    public AccountMirrorPublicationTests()
    {
        _db = new PicerDatabase(Path.Combine(_root, "mirror.db"));
        _db.SaveMirrorCheckpoint(new("fixture-epoch", 5, 7));
        _db.UpsertMirroredPerson(new(1, "fixture", 0, 0));
        _db.MergeMetadataEdits([new MetadataEdit("person", 1, "name", "changed", DateTime.UtcNow.ToString("O"), Guid.NewGuid().ToString("N"))]);
    }

    [Fact]
    public async Task AccountRestartPublishesEditsWithoutUploadingAggregateOrWritingLegacyCheckpoint()
    {
        var server = new List<MetadataEdit>();
        var posts = 0;
        var checkpoint = Path.Combine(_root, "legacy.json");
        for (var restart = 0; restart < 2; restart++)
        {
            using var client = Create(async request =>
            {
                Assert.Equal("/api/sync/v1/metadata-edits", request.RequestUri!.AbsolutePath);
                if (request.Method == HttpMethod.Post)
                {
                    posts++;
                    Assert.Equal("fixture-epoch", Assert.Single(request.Headers.GetValues("X-Picer-Mirror-Epoch")));
                    var edits = await request.Content!.ReadFromJsonAsync<MetadataEdit[]>() ?? throw new InvalidDataException();
                    Assert.Single(edits);
                    server.Add(edits[0] with { ServerVersion = 8 });
                }
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(server) };
            });
            var result = await client.PushIncrementalAsync(_db, checkpoint);
            Assert.Equal(restart == 0, result.Changed);
            Assert.Equal(0, result.Images);
            Assert.Equal(0, result.Videos);
            Assert.False(File.Exists(checkpoint));
            await client.PushMetadataAsync(_db);
            await client.PushDirectoryClassificationAsync(new Dictionary<string, bool> { [_root] = true });
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.PushImagesAsync(_db));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.PushVideosAsync(_db));
        }
        Assert.Equal(1, posts);
        Assert.Empty(_db.GetPendingMetadataEdits());
        Assert.Equal(7, _db.GetMirrorCheckpoint()!.MetadataRevision);
    }

    [Fact]
    public async Task AccountManualMetadataIsAcknowledgedBeforeFaceReviewReplay()
    {
        var source = _db.AddSource(_root);
        var photo = _db.UpsertImage(new Picer.Core.Models.ImageRecord
        {
            SourceId = source.Id, Path = Path.Combine(_root, "fixture.jpg"),
            ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow
        });
        _db.UpsertMirroredFaces([new GatewayDetectedFace(1, photo.Id, "fixture-model", 0,
            .1, .1, .2, .2, .99, null, null, DateTime.UtcNow.ToString("O"))], []);
        new Picer.Core.People.PeopleService(_db).IgnoreFace(1);
        var acknowledged = new List<MetadataEdit>();
        var reviewSent = false;
        using var client = Create(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/sync/v1/face-review-decisions")
            {
                Assert.NotEmpty(acknowledged);
                Assert.Empty(_db.GetPendingMetadataEdits());
                var decisions = await request.Content!.ReadFromJsonAsync<GatewayFaceReviewDecision[]>() ?? throw new InvalidDataException();
                Assert.Equal("ignored", Assert.Single(decisions).State);
                reviewSent = true;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(decisions) };
            }
            Assert.Equal("/api/sync/v1/metadata-edits", request.RequestUri.AbsolutePath);
            if (request.Method == HttpMethod.Post)
            {
                var edits = await request.Content!.ReadFromJsonAsync<MetadataEdit[]>() ?? throw new InvalidDataException();
                acknowledged.AddRange(edits.Select((edit, index) => edit with { ServerVersion = 10 + index }));
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(acknowledged) };
        });
        await client.PushIncrementalAsync(_db, Path.Combine(_root, "legacy.json"));
        Assert.True(reviewSent);
    }

    [Fact]
    public async Task ReadOnlyAccountCannotPublishPendingEditsOrLegacyConfiguration()
    {
        var requests = 0;
        using var client = Create(request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/sync/v1/metadata-edits", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<MetadataEdit>()) });
        }, canWrite: false);
        Assert.False((await client.PushIncrementalAsync(_db, Path.Combine(_root, "legacy.json"))).Changed);
        await client.PushDirectoryClassificationAsync(new Dictionary<string, bool> { [_root] = true });
        Assert.Equal(1, requests);
        Assert.Single(_db.GetPendingMetadataEdits());
    }

    [Fact]
    public async Task FailedEditUploadRetainsOperationForRetryWithoutPublishingSnapshot()
    {
        var fail = true;
        var original = Assert.Single(_db.GetPendingMetadataEdits());
        using var client = Create(async request =>
        {
            Assert.Equal("/api/sync/v1/metadata-edits", request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post)
            {
                var sent = await request.Content!.ReadFromJsonAsync<MetadataEdit[]>() ?? throw new InvalidDataException();
                Assert.Equal(original.OperationId, Assert.Single(sent).OperationId);
                if (fail) return new(HttpStatusCode.ServiceUnavailable);
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { original with { ServerVersion = 9 } }) };
        });
        var checkpoint = Path.Combine(_root, "legacy.json");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.PushIncrementalAsync(_db, checkpoint));
        Assert.Equal(original.OperationId, Assert.Single(_db.GetPendingMetadataEdits()).OperationId);
        Assert.False(File.Exists(checkpoint));
        fail = false;
        Assert.True((await client.PushIncrementalAsync(_db, checkpoint)).Changed);
        Assert.Empty(_db.GetPendingMetadataEdits());
    }

    private static IndexSyncClient Create(Func<HttpRequestMessage, Task<HttpResponseMessage>> send, bool canWrite = true)
        => new(new HttpClient(new Handler(send)) { BaseAddress = new Uri("https://fixture.invalid/") }, accountMode: true, canWrite: canWrite);

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
