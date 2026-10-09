using System.Net;
using System.Text;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.People;
using Picer.Core.Video;

namespace Picer.Tests.Gateway;

public sealed class VideoMetadataSyncIdentityTests
{
    [Fact]
    public async Task SyncDefersLocalVideoIdentityThenSendsSameOperationAfterVerification()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-identity-wire-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new PicerDatabase(Path.Combine(root, "test.db"));
            var source = db.AddSource(root).Id;
            var videos = new VideoRepository(db);
            var unknown = new VideoRecord(100, source, Path.Combine(root, "unknown.mp4"), 3, DateTime.UnixEpoch,
                null, 0, 0, null, null, "active");
            videos.UpsertMirrored(unknown, source);
            videos.UpsertMirrored(unknown with { Id = 200, Path = Path.Combine(root, "verified.mp4"), Sha256 = new string('b', 64) }, source);
            var metadata = new VideoSearchMetadata(db);
            metadata.SetConcept(100, "animal", "manual"); metadata.SetConcept(200, "animal", "manual");
            var people = new PeopleService(db); var person = people.Create("test"); people.Rename(person.Id, "updated");
            var pendingOperation = db.GetMetadataEdits().Single(edit => edit.Kind == "video-concept" && edit.EntityId == 100).OperationId;
            // Creating and renaming a person also records both aliases.
            var personOperations = db.GetMetadataEdits().Where(edit => edit.Kind == "person").Select(edit => edit.OperationId).Order().ToArray();
            using var handler = new MetadataHandler();
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
            using var sync = new IndexSyncClient(http);
            await sync.PushMetadataEditsAsync(db);
            var first = Assert.Single(handler.Batches);
            Assert.Equal(personOperations.Length + 1, first.Length);
            Assert.Equal(personOperations, first.Where(edit => edit.Kind == "person").Select(edit => edit.OperationId).Order().ToArray());
            var verifiedVideo = Assert.Single(first.Where(edit => edit.Kind == "video-concept"));
            Assert.Equal(200, verifiedVideo.EntityId);
            Assert.Equal("sha256:" + new string('b', 64), verifiedVideo.VideoContentIdentity);
            Assert.DoesNotContain(first, edit => edit.OperationId == pendingOperation);
            Assert.All(first.Where(edit => edit.Kind == "video-concept"), edit => Assert.StartsWith("sha256:", edit.VideoContentIdentity!));
            videos.UpsertMirrored(unknown with { Sha256 = new string('a', 64) }, source);
            await sync.PushMetadataEditsAsync(db);
            Assert.Equal(2, handler.Batches.Count);
            Assert.Equal(first.Select(edit => edit.OperationId).Append(pendingOperation).Order().ToArray(),
                handler.Batches[1].Select(edit => edit.OperationId).Order().ToArray());
            Assert.Contains(handler.Batches[1], edit => edit.OperationId == pendingOperation && edit.VideoContentIdentity == "sha256:" + new string('a', 64));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public List<MetadataEdit[]> Batches { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/sync/v1/metadata-edits", request.RequestUri!.AbsolutePath);
            var payload = "[]";
            if (request.Method == HttpMethod.Post)
            {
                payload = await request.Content!.ReadAsStringAsync(cancellationToken);
                Batches.Add(JsonSerializer.Deserialize<MetadataEdit[]>(payload, Json)!);
            }
            else Assert.Equal(HttpMethod.Get, request.Method);
            return new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }
}
