using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Search;
using Picer.Core.Video;

namespace Picer.Core.Gateway;

public sealed class IndexSyncClient(HttpClient client)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IndexSyncClient Create(string baseUrl, string token, byte[]? expectedSpkiPin = null)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                if (expectedSpkiPin is null) return true;
                var actual = new X509Certificate2(certificate).PublicKey.ExportSubjectPublicKeyInfo();
                return CryptographicOperations.FixedTimeEquals(actual, expectedSpkiPin);
            }
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new IndexSyncClient(http);
    }

    public async Task<int> PushImagesAsync(PicerDatabase database, IProgress<(int done, int total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var sources = database.GetSources();
        var total = sources.Sum(source => database.GetImages(source.Id, includeMissing: true).Count);
        var done = 0;
        foreach (var source in sources)
        {
            var images = database.GetImages(source.Id, includeMissing: true);
            foreach (var batch in images.Chunk(200))
            {
                using var response = await client.PostAsJsonAsync("api/sync/v1/images", new { sourcePath = source.Path, images }, Json, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Image sync failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}");
                done += batch.Length;
                progress?.Report((done, total));
            }
        }
        return done;
    }

    public async Task<int> PushVideosAsync(PicerDatabase database, IProgress<(int done, int total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var repository = new VideoRepository(database);
        var sources = database.GetSources();
        var done = 0;
        foreach (var source in sources)
        {
            var videos = repository.GetAll().Where(video => video.SourceId == source.Id).ToArray();
            foreach (var batch in videos.Chunk(100))
            {
                using var response = await client.PostAsJsonAsync("api/sync/v1/videos", new { sourcePath = source.Path, videos = batch }, Json, cancellationToken);
                response.EnsureSuccessStatusCode();
                done += batch.Length;
                progress?.Report((done, videos.Length));
            }
        }
        return done;
    }

    public async Task PushMetadataAsync(PicerDatabase database, CancellationToken cancellationToken = default)
    {
        var albums = SemanticEngine.GetAlbums(database);
        var memberships = albums.SelectMany(album =>
            SemanticEngine.GetAlbumImages(database, album.Id).Select(image => new GatewayAlbumImageLink(album.Id, image.Id))).ToArray();
        var tagLinks = database.GetImages(includeMissing: true)
            .SelectMany(image => SemanticEngine.GetImageTags(database, image.Id)
                .Select(tag => new GatewayImageTagLink(image.Id, tag.Id))).ToArray();
        var request = new SyncMetadataRequest(
            SemanticEngine.GetTags(database), albums, memberships,
            tagLinks, database.GetImageLocationHierarchyRows(), new PeopleService(database).GetAll(), null, null,
            database.GetAllMirroredVideoKeyframes(), database.GetAllMirroredVideoTranscripts(), database.GetAllMirroredVideoSummaries(), database.GetAllMirroredVideoFaces());
        using var response = await client.PostAsJsonAsync("api/sync/v1/metadata", request, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        var faces = database.GetAllMirroredFaces();
        var assignments = database.GetAllMirroredFaceAssignments().ToDictionary(item => item.FaceId);
        foreach (var batch in faces.Chunk(2000))
        {
            var batchAssignments = batch.Where(face => assignments.TryGetValue(face.Id, out _))
                .Select(face => assignments[face.Id]).ToArray();
            using var faceResponse = await client.PostAsJsonAsync("api/sync/v1/faces",
                new { faces = batch, assignments = batchAssignments }, Json, cancellationToken);
            faceResponse.EnsureSuccessStatusCode();
        }
    }

    private sealed record SyncMetadataRequest(
        IReadOnlyList<Tag> Tags,
        IReadOnlyList<Album> Albums,
        IReadOnlyList<GatewayAlbumImageLink> AlbumImages,
        IReadOnlyList<GatewayImageTagLink> ImageTags,
        IReadOnlyList<ImageLocationHierarchyRow> Locations,
        IReadOnlyList<PersonSummary> People,
        IReadOnlyList<GatewayDetectedFace>? Faces = null,
        IReadOnlyList<GatewayFaceAssignment>? FaceAssignments = null,
        IReadOnlyList<GatewayVideoKeyframe>? VideoKeyframes = null,
        IReadOnlyList<GatewayVideoTranscript>? VideoTranscripts = null,
        IReadOnlyList<GatewayVideoSummary>? VideoSummaries = null,
        IReadOnlyList<GatewayVideoFace>? VideoFaces = null);

}
