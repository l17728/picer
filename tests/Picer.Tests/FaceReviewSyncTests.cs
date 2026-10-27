using System.Net;
using System.Net.Http.Json;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.People;

namespace Picer.Tests;

public sealed class FaceReviewSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "face-review-sync-" + Guid.NewGuid().ToString("N"));
    private static GatewayMetadata Empty => new([], [], [], [], [], [], [], []);

    private PicerDatabase Open(string name)
    {
        Directory.CreateDirectory(_root);
        var db = new PicerDatabase(Path.Combine(_root, name));
        if (db.GetAllMirroredFaces().Count == 0)
        {
            var source = db.AddSource(_root);
            var image = db.UpsertImage(new ImageRecord { SourceId = source.Id, Path = Path.Combine(_root, "photo.jpg"),
                Status = ImageStatus.Active, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow });
            db.UpsertMirroredFaces([new GatewayDetectedFace(1, image.Id, "insightface-arcface", 0,
                .1, .1, .2, .2, .99, null, null, DateTime.UtcNow.ToString("O"))], []);
        }
        return db;
    }

    [Fact]
    public void RestartAndOldSnapshot_RestorePreviouslyLostIgnoreFromJournal()
    {
        using (var db = Open("local.db"))
        {
            new PeopleService(db).IgnoreFace(1);
            using var simulateOldBug = db.Command("DELETE FROM face_review_states");
            simulateOldBug.ExecuteNonQuery();
        }
        using var reopened = Open("local.db");
        reopened.ReplaceMirroredFaceWorkflow(Empty);
        Assert.Single(reopened.GetAllMirroredFaceReviewStates());
        Assert.Equal("ignored", reopened.GetFaceReviewDecisions().Single().State);
    }

    [Fact]
    public void RestoreTombstone_SurvivesStaleIgnoreAndEmptySnapshotsOnBothPeers()
    {
        using var local = Open("local.db");
        using var server = Open("server.db");
        new PeopleService(local).IgnoreFace(1);
        var ignored = local.GetFaceReviewDecisions();
        server.MergeFaceReviewDecisions(ignored);
        server.ReplaceMirroredFaceWorkflow(Empty);
        Assert.Single(server.GetAllMirroredFaceReviewStates());
        new PeopleService(local).MoveFaceToUnassigned(1);
        var restored = local.GetFaceReviewDecisions();
        server.MergeFaceReviewDecisions(restored);
        server.MergeFaceReviewDecisions(ignored);
        server.MergeFaceReviewDecisions(restored);
        var oldSnapshot = Empty with { FaceReviewStates = [new(1, "ignored", ignored[0].UpdatedUtc)] };
        local.ReplaceMirroredFaceWorkflow(oldSnapshot);
        server.ReplaceMirroredFaceWorkflow(oldSnapshot);
        Assert.Empty(local.GetAllMirroredFaceReviewStates());
        Assert.Empty(server.GetAllMirroredFaceReviewStates());
        using var count = server.Command("SELECT COUNT(*) FROM face_corrections");
        Assert.Equal(2L, count.ExecuteScalar());
    }

    [Fact]
    public async Task FailedUpload_RetainsDecisionAndRetriesWithoutUploadingVectors()
    {
        using var local = Open("local.db");
        using var server = Open("server.db");
        new PeopleService(local).IgnoreFace(1);
        var calls = 0;
        using var handler = new Handler(async request =>
        {
            Assert.EndsWith("/api/sync/v1/face-review-decisions", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("embedding", body, StringComparison.OrdinalIgnoreCase);
            if (++calls == 1) return new HttpResponseMessage(HttpStatusCode.BadGateway);
            var decisions = await request.Content.ReadFromJsonAsync<GatewayFaceReviewDecision[]>();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(server.MergeFaceReviewDecisions(decisions!)) };
        });
        using var sync = new IndexSyncClient(new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") });
        await Assert.ThrowsAsync<HttpRequestException>(() => sync.PushFaceReviewDecisionsAsync(local));
        local.ReplaceMirroredFaceWorkflow(Empty);
        Assert.Single(local.GetAllMirroredFaceReviewStates());
        await sync.PushFaceReviewDecisionsAsync(local);
        await sync.PushFaceReviewDecisionsAsync(local);
        Assert.Equal(2, calls);
        server.ReplaceMirroredFaceWorkflow(Empty);
        Assert.Single(server.GetAllMirroredFaceReviewStates());
    }

    [Fact]
    public void InvalidBatch_RollsBackEarlierDecisions()
    {
        using var db = Open("server.db");
        var valid = new GatewayFaceReviewDecision(1, "ignored", DateTime.UtcNow.ToString("O"));
        Assert.Throws<ArgumentException>(() => db.MergeFaceReviewDecisions([valid, valid with { State = "invalid" }]));
        Assert.Empty(db.GetFaceReviewDecisions());
        Assert.Empty(db.GetAllMirroredFaceReviewStates());
    }

    [Fact]
    public void ConfirmingIgnoredFace_RecordsRestoreAndRejectsOlderIgnore()
    {
        using var db = Open("local.db");
        var people = new PeopleService(db);
        people.IgnoreFace(1);
        var old = db.GetFaceReviewDecisions();
        people.CreateAndAssignFaces("测试人物", [1]);
        db.MergeFaceReviewDecisions(old);
        db.ReplaceMirroredFaceWorkflow(Empty);
        Assert.Empty(db.GetAllMirroredFaceReviewStates());
        Assert.Equal("active", db.GetFaceReviewDecisions().Single().State);
        Assert.Single(db.GetAllMirroredFaceAssignments());
    }

    [Fact]
    public async Task EditDuringAcknowledgment_IsNotLostOrMarkedAsAlreadySent()
    {
        using var local = Open("local.db");
        using var server = Open("server.db");
        new PeopleService(local).IgnoreFace(1);
        var calls = 0;
        using var handler = new Handler(async request =>
        {
            var sent = await request.Content!.ReadFromJsonAsync<GatewayFaceReviewDecision[]>();
            if (++calls == 1) new PeopleService(local).MoveFaceToUnassigned(1);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(server.MergeFaceReviewDecisions(sent!)) };
        });
        using var sync = new IndexSyncClient(new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") });
        await sync.PushFaceReviewDecisionsAsync(local);
        Assert.Empty(local.GetAllMirroredFaceReviewStates());
        await sync.PushFaceReviewDecisionsAsync(local);
        Assert.Equal(2, calls);
        Assert.Empty(server.GetAllMirroredFaceReviewStates());
    }

    [Fact]
    public void AcknowledgmentDoesNotReplayUnrelatedFaceDecisions()
    {
        using var db = Open("bounded-ack.db");
        var face = db.GetAllMirroredFaces().Single();
        db.UpsertMirroredFaces([face with { Id = 2, Ordinal = 1 }], []);
        var timestamp = DateTime.UtcNow.ToString("O");
        db.MergeFaceReviewDecisions([new(1, "ignored", timestamp), new(2, "active", timestamp)]);
        using (var seed = db.Command("INSERT INTO face_review_states(face_id,state,updated_utc) VALUES(2,'ignored','2026-10-27'); CREATE TRIGGER reject_unrelated_ack BEFORE DELETE ON face_review_states WHEN OLD.face_id=2 BEGIN SELECT RAISE(ABORT,'unrelated_ack_replay'); END;")) seed.ExecuteNonQuery();
        var acknowledged = db.MergeFaceReviewDecisions([new(1, "ignored", timestamp)]);
        Assert.Equal(1, Assert.Single(acknowledged).FaceId);
        Assert.Equal(2, db.GetAllMirroredFaceReviewStates().Count);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
