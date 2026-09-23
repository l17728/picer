using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Search;
using Picer.Core.Video;

namespace Picer.Core.Gateway;

public sealed class IndexSyncClient(HttpClient client) : IDisposable
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
        // Face workflow projections reference detected_faces. Upload the face
        // rows first so a first-time mirror can apply review/candidate state
        // atomically without foreign-key failures.
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
        var request = BuildMetadataRequest(database);
        using var response = await client.PostAsJsonAsync("api/sync/v1/metadata", request, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Pushes only when the local logical index changed since the last
    /// successful push. The server upserts each entity idempotently, so a
    /// retry or a second client cannot duplicate records.
    /// </summary>
    public async Task<IndexSyncResult> PushIncrementalAsync(PicerDatabase database, string checkpointPath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var metadata = BuildMetadataRequest(database);
        var metadataFingerprint = Fingerprint(metadata with
        {
            Faces = metadata.Faces?.Select(face => face with { Embedding = null }).ToArray(),
            VideoFaces = metadata.VideoFaces?.Select(face => face with { Embedding = null }).ToArray()
        });
        var mediaFingerprint = Fingerprint(database.GetSources().Select(source => new
        {
            source.Id, source.Path,
            Images = database.GetImages(source.Id, includeMissing: true).Select(image => new
            {
                image.Id, image.Path, image.FileSize, image.ModifiedUtc, image.Sha256,
                image.Status, image.Width, image.Height, image.TakenUtc, image.Camera, image.Gps,
                image.ThumbPath, image.AverageHash, image.DifferenceHash
            }).ToArray(),
            Videos = new VideoRepository(database).GetAll().Where(video => video.SourceId == source.Id)
                .Select(video => new { video.Id, video.Path, video.FileSize, video.ModifiedUtc }).ToArray()
        }).ToArray());
        var previous = LoadCheckpoint(checkpointPath);
        var pushedImages = 0;
        var pushedVideos = 0;
        if (!string.Equals(previous.MediaFingerprint, mediaFingerprint, StringComparison.Ordinal))
        {
            progress?.Report("正在增量同步图片与视频…");
            pushedImages = await PushImagesAsync(database, cancellationToken: cancellationToken).ConfigureAwait(false);
            pushedVideos = await PushVideosAsync(database, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        if (!string.Equals(previous.MetadataFingerprint, metadataFingerprint, StringComparison.Ordinal))
        {
            progress?.Report("正在增量同步人物、标签和分析元数据…");
            await PushMetadataAsync(database, cancellationToken).ConfigureAwait(false);
        }
        var changed = !string.Equals(previous.MediaFingerprint, mediaFingerprint, StringComparison.Ordinal)
            || !string.Equals(previous.MetadataFingerprint, metadataFingerprint, StringComparison.Ordinal);
        if (changed)
            SaveCheckpoint(checkpointPath, new SyncCheckpoint(mediaFingerprint, metadataFingerprint, DateTimeOffset.UtcNow));
        return new IndexSyncResult(changed, pushedImages, pushedVideos, metadataFingerprint, mediaFingerprint);
    }

    public async Task PushDirectoryClassificationAsync(IReadOnlyDictionary<string, bool> overrides,
        CancellationToken cancellationToken = default)
    {
        using var response = await client.PostAsJsonAsync("api/sync/v1/directory-classification",
            new { overrides }, Json, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetDirectoryClassificationAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync("api/gateway/v1/directory-classification", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Dictionary<string, bool>>(Json, cancellationToken).ConfigureAwait(false)
            ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    }

    private SyncMetadataRequest BuildMetadataRequest(PicerDatabase database)
    {
        var faces = database.GetAllMirroredFaces();
        var assignments = database.GetAllMirroredFaceAssignments().ToDictionary(item => item.FaceId);
        var albums = SemanticEngine.GetAlbums(database);
        var memberships = albums.SelectMany(album =>
            SemanticEngine.GetAlbumImages(database, album.Id).Select(image => new GatewayAlbumImageLink(album.Id, image.Id))).ToArray();
        var tagLinks = database.GetImages(includeMissing: true)
            .SelectMany(image => SemanticEngine.GetImageTags(database, image.Id)
                .Select(tag => new GatewayImageTagLink(image.Id, tag.Id))).ToArray();
        return new SyncMetadataRequest(
            SemanticEngine.GetTags(database), albums, memberships,
            tagLinks, database.GetImageLocationHierarchyRows(), new PeopleService(database).GetAll(), faces, assignments.Values.ToArray(),
            database.GetAllMirroredVideoKeyframes(), database.GetAllMirroredVideoTranscripts(), database.GetAllMirroredVideoSummaries(), database.GetAllMirroredVideoFaces(),
            database.GetAllMirroredCloudVisionAnalyses(), database.GetAllMirroredSceneGroupDescriptions(),
            database.GetAllMirroredFaceReviewStates(), database.GetAllMirroredFaceCandidateClusters(), database.GetAllMirroredFaceCandidateMembers());
    }

    private static string Fingerprint<T>(T value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static SyncCheckpoint LoadCheckpoint(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<SyncCheckpoint>(File.ReadAllText(path), Json) ?? new("", "", DateTimeOffset.MinValue) : new("", "", DateTimeOffset.MinValue); }
        catch { return new("", "", DateTimeOffset.MinValue); }
    }

    private static void SaveCheckpoint(string path, SyncCheckpoint checkpoint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(checkpoint, Json));
        File.Move(temp, path, true);
    }

    private sealed record SyncCheckpoint(string MediaFingerprint, string MetadataFingerprint, DateTimeOffset UpdatedUtc);
    public sealed record IndexSyncResult(bool Changed, int Images, int Videos, string MetadataFingerprint, string MediaFingerprint);

    public void Dispose() => client.Dispose();

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
        IReadOnlyList<GatewayVideoFace>? VideoFaces = null,
        IReadOnlyList<GatewayCloudVisionAnalysis>? CloudVisionAnalyses = null,
        IReadOnlyList<GatewaySceneGroupDescription>? SceneGroupDescriptions = null,
        IReadOnlyList<GatewayFaceReviewState>? FaceReviewStates = null,
        IReadOnlyList<GatewayFaceCandidateCluster>? FaceCandidateClusters = null,
        IReadOnlyList<GatewayFaceCandidateMember>? FaceCandidateMembers = null);

}
