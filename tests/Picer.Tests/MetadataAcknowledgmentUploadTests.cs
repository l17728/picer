using System.Net;
using System.Net.Http.Json;
using Picer.Core.Data;
using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class MetadataAcknowledgmentUploadTests
{
    [Fact]
    public async Task AccountRestartDoesNotUploadDurablyAcknowledgedEditsAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), "metadata-ack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var db = new PicerDatabase(Path.Combine(root, "mirror.db"));
            db.SaveMirrorCheckpoint(new("fixture-epoch", 0, 0));
            db.UpsertMirroredPerson(new(1, "first", 0, 0));
            db.UpsertMirroredPerson(new(2, "second", 0, 0));
            var timestamp = DateTime.UtcNow.ToString("O");
            var acknowledged = new MetadataEdit("person", 1, "name", "first", timestamp, Guid.NewGuid().ToString("N")) { ServerVersion = 1 };
            var pending = new MetadataEdit("person", 2, "name", "changed", timestamp, Guid.NewGuid().ToString("N"));
            db.MergeMetadataEdits([acknowledged, pending]);
            var uploaded = new List<long>();
            var server = new List<MetadataEdit> { acknowledged };
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var handler = new Handler(async request =>
                {
                    if (request.Method == HttpMethod.Post)
                    {
                        Assert.Equal("fixture-epoch", Assert.Single(request.Headers.GetValues("X-Picer-Mirror-Epoch")));
                        var sent = await request.Content!.ReadFromJsonAsync<MetadataEdit[]>() ?? throw new InvalidDataException("missing_test_payload");
                        uploaded.AddRange(sent.Select(edit => edit.EntityId));
                        var accepted = sent.Select(edit => edit with { ServerVersion = 2 }).ToArray();
                        server.AddRange(accepted);
                        return new(HttpStatusCode.OK) { Content = JsonContent.Create(accepted) };
                    }
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(server) };
                });
                using var sync = new IndexSyncClient(new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") }, accountMode: true);
                await sync.PushMetadataEditsAsync(db);
            }
            Assert.Equal(new long[] { 2 }, uploaded);
            Assert.All(db.GetMetadataEdits(), edit => Assert.True(edit.ServerVersion > 0));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
