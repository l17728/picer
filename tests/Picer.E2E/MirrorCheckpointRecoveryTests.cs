using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using Picer.App;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.E2E;

public sealed class MirrorCheckpointRecoveryTests
{
    [Fact]
    public async Task InterruptedPageDoesNotCheckpointAndRestartReplaysThenPropagatesRenameAndDeletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-mirror-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var oldDb = Get(nameof(AppServices.Db));
        var oldVideos = Get(nameof(AppServices.Videos));
        var oldMode = Get(nameof(AppServices.ServiceMode));
        PicerDatabase? mirror = null;
        try
        {
            using var authority = new PicerDatabase(Path.Combine(root, "server.db"));
            var source = authority.AddSource(Path.Combine(root, "media"));
            foreach (var id in new[] { 11, 22 }) authority.UpsertMirroredImage(new()
            {
                Id = id, SourceId = source.Id, Path = Path.Combine(source.Path, id + ".jpg"),
                ModifiedUtc = DateTime.UnixEpoch, FileSize = 1, Status = ImageStatus.Missing
            });
            var mirrorPath = Path.Combine(root, "mirror.db");
            mirror = new PicerDatabase(mirrorPath);
            Set(nameof(AppServices.Db), mirror); Set(nameof(AppServices.Videos), new VideoRepository(mirror)); Set(nameof(AppServices.ServiceMode), true);
            using var handler = new FeedHandler(authority) { FailSecondPage = true };
            using var http = new HttpClient(handler);
            using var gateway = new HttpIndexGateway("https://fixture.invalid", "fixture", client: http);
            await Assert.ThrowsAsync<HttpRequestException>(() => AppServices.RefreshChangedMediaAsync(gateway, authority.GetSources(), CancellationToken.None));
            Assert.Null(mirror.GetMirrorCheckpoint());
            Assert.Single(mirror.GetImages(includeMissing: true));
            var priorB = authority.GetImage(22)!;
            priorB.SourceId = mirror.GetSources().Single().Id;
            mirror.UpsertMirroredImage(priorB);
            mirror.UpsertMirroredTag(new Picer.Core.Search.Tag { Id = 900, Name = "retained-local-fixture" });
            mirror.UpsertMirroredImageTag(22, 900);
            handler.BeforeSecondPage = () =>
            {
                using var update = authority.Command("UPDATE images SET path=$path WHERE id=22");
                update.Parameters.AddWithValue("$path", Path.Combine(source.Path, "concurrent.jpg")); update.ExecuteNonQuery();
            };
            handler.FailSecondPage = false;
            var first = await AppServices.RefreshChangedMediaAsync(gateway, authority.GetSources(), CancellationToken.None);
            Assert.True(first.Supported);
            Assert.Equal(2, mirror.GetImageCount(includeMissing: true));
            Assert.EndsWith("concurrent.jpg", mirror.GetImage(22)!.Path);
            using (var links = mirror.Command("SELECT COUNT(*) FROM image_tags WHERE image_id=22"))
                Assert.Equal(1L, links.ExecuteScalar()); // Unread post-Through rows must never be purged.
            var checkpoint = Assert.IsType<MirrorCheckpoint>(mirror.GetMirrorCheckpoint());
            Assert.Equal(0, checkpoint.MetadataRevision); // Media completion cannot acknowledge metadata.
            mirror.Dispose(); mirror = new PicerDatabase(mirrorPath);
            Set(nameof(AppServices.Db), mirror); Set(nameof(AppServices.Videos), new VideoRepository(mirror));
            Assert.Equal(checkpoint, mirror.GetMirrorCheckpoint());
            using (var update = authority.Command("UPDATE images SET path=$path WHERE id=11"))
            { update.Parameters.AddWithValue("$path", Path.Combine(source.Path, "moved.jpg")); update.ExecuteNonQuery(); }
            authority.DeleteImage(22);
            handler.Requests.Clear();
            await AppServices.RefreshChangedMediaAsync(gateway, authority.GetSources(), CancellationToken.None);
            Assert.Equal(checkpoint.MediaRevision, handler.Requests[0].After);
            Assert.EndsWith("moved.jpg", mirror.GetImage(11)!.Path);
            Assert.Null(mirror.GetImage(22));
            var unchanged = await AppServices.RefreshChangedMediaAsync(gateway, authority.GetSources(), CancellationToken.None);
            Assert.Equal(mirror.GetMirrorCheckpoint()!.MediaRevision, unchanged.Checkpoint!.MediaRevision);
            // A separate local index must not be overwritten by matching remote numbers.
            Set(nameof(AppServices.ServiceMode), false);
            using (var reset = mirror.Command("DELETE FROM mirror_checkpoint; DELETE FROM mirror_owned_media; UPDATE images SET path=$path WHERE id=11"))
            { reset.Parameters.AddWithValue("$path", Path.Combine(root, "local-only.jpg")); reset.ExecuteNonQuery(); }
            await Assert.ThrowsAsync<InvalidDataException>(() => AppServices.RefreshChangedMediaAsync(gateway, authority.GetSources(), CancellationToken.None));
            Assert.EndsWith("local-only.jpg", mirror.GetImage(11)!.Path);
            Assert.Null(mirror.GetMirrorCheckpoint());
        }
        finally
        {
            Set(nameof(AppServices.Db), oldDb); Set(nameof(AppServices.Videos), oldVideos); Set(nameof(AppServices.ServiceMode), oldMode);
            mirror?.Dispose(); Directory.Delete(root, true);
        }
    }

    private static object? Get(string name) => typeof(AppServices).GetProperty(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
    private static void Set(string name, object? value) => typeof(AppServices).GetProperty(name, BindingFlags.Public | BindingFlags.Static)!.SetValue(null, value);

    private sealed class FeedHandler(PicerDatabase authority) : HttpMessageHandler
    {
        public bool FailSecondPage { get; set; }
        public Action? BeforeSecondPage { get; set; }
        public List<GatewayChangeRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                var id = long.Parse(request.RequestUri!.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
                var image = authority.GetImage(id);
                return image is null ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = JsonContent.Create(image) };
            }
            var query = (await request.Content!.ReadFromJsonAsync<GatewayChangeRequest>(cancellationToken))!;
            Requests.Add(query);
            if (FailSecondPage && query.Through is not null) return new(HttpStatusCode.ServiceUnavailable);
            if (query.Through is not null && BeforeSecondPage is { } mutate)
            { BeforeSecondPage = null; mutate(); }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(authority.ReadMirrorChanges(query with { PageSize = 1 })) };
        }
    }
}
