using System.Net;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;

namespace Picer.Tests.Gateway;

public sealed class IndexSyncBatchTests
{
    [Fact]
    public async Task PushImages_SendsEachImageOnceInBoundedBatches()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-sync-batches-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new PicerDatabase(Path.Combine(root, "index.db"));
            var source = database.AddSource(root);
            var ids = new List<long>();
            using (var transaction = database.BeginTransaction())
            {
                for (var index = 0; index < 401; index++)
                {
                    var image = new ImageRecord
                    {
                        SourceId = source.Id, Path = Path.Combine(root, $"photo-{index}.jpg"),
                        FileSize = 1, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow,
                        Status = ImageStatus.Active
                    };
                    database.UpsertImageInTransaction(image, transaction);
                    ids.Add(image.Id);
                }
                transaction.Commit();
            }
            using var handler = new BatchHandler();
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
            using var sync = new IndexSyncClient(http);

            var pushed = await sync.PushImagesAsync(database);

            Assert.Equal(401, pushed);
            Assert.Equal(new[] { 200, 200, 1 }, handler.Batches.Select(batch => batch.Length));
            var sent = handler.Batches.SelectMany(batch => batch).ToArray();
            Assert.Equal(401, sent.Distinct().Count());
            Assert.Equal(ids.Order(), sent.Order());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class BatchHandler : HttpMessageHandler
    {
        public List<long[]> Batches { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/sync/v1/images", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var images = document.RootElement.GetProperty("images").EnumerateArray()
                .Select(image => image.GetProperty("id").GetInt64()).ToArray();
            Assert.InRange(images.Length, 1, 200);
            Batches.Add(images);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
