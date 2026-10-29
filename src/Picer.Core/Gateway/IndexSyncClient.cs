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
using Picer.Core.Vision;

namespace Picer.Core.Gateway;

public sealed class IndexSyncClient(HttpClient client, bool accountMode = false, bool canWrite = true,
    Func<Action, CancellationToken, Task>? databaseWork = null) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? _lastReviewFingerprint;
    private readonly SemaphoreSlim _reviewSyncGate = new(1, 1);
    private readonly SemaphoreSlim _metadataSyncGate = new(1, 1);
    private readonly SemaphoreSlim _descriptionSyncGate = new(1, 1);
    private const int MergeBatchSize = 100;
    private HashSet<string> _acknowledgedMetadataOperations = new(StringComparer.Ordinal);
    public IReadOnlySet<string> AcknowledgedMetadataOperations => Volatile.Read(ref _acknowledgedMetadataOperations);

    private Task WriteDatabaseAsync(Action work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (databaseWork is not null) return databaseWork(work, cancellationToken);
        work();
        return Task.CompletedTask;
    }

    private async Task MergeMetadataAsync(PicerDatabase database, IReadOnlyList<MetadataEdit> edits,
        CancellationToken cancellationToken)
    {
        // Do not hold SQLite's sole writer for an entire cold-start snapshot.
        // Each batch commits before yielding the shared UI database scheduler.
        foreach (var batch in edits.Chunk(MergeBatchSize))
            await WriteDatabaseAsync(() => database.MergeMetadataEdits(batch, applyOnlyChangedFields: true),
                cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyLearnedTrainingArtifactAsync(PicerDatabase database, LearnedTrainingApplication application, CancellationToken cancellationToken = default,
        TrainingApplicationBaseline? baseline = null)
    {
        if (!canWrite) throw new UnauthorizedAccessException("此设备没有编辑照片空间的权限。");
        var epoch = baseline?.Epoch ?? database.GetMirrorCheckpoint()?.Epoch ?? throw new InvalidOperationException("请先刷新照片空间索引。");
        var active = baseline?.ActiveArtifact ?? new VisionAnalysisRepository(database).GetActiveFaceAdapterVersion(application.ModelVersion);
        using var response = await PostMirrorEditAsync("api/service/v1/faces/training/apply-learned",
            new LearnedTrainingMutation(application, epoch, active), database, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new InvalidOperationException("照片空间的训练制品或人物映射已变化，请刷新后重试。");
        response.EnsureSuccessStatusCode();
    }

    public async Task ApplyTrainingArtifactAsync(PicerDatabase database, TrainingArtifactData artifact, CancellationToken cancellationToken = default,
        TrainingApplicationBaseline? baseline = null)
    {
        if (!canWrite) throw new UnauthorizedAccessException("此设备没有编辑照片空间的权限。");
        var epoch = baseline?.Epoch ?? database.GetMirrorCheckpoint()?.Epoch ?? throw new InvalidOperationException("请先刷新照片空间索引。");
        var active = baseline?.ActiveArtifact ?? new VisionAnalysisRepository(database).GetActiveFaceAdapterVersion(artifact.ModelVersion);
        using var response = await PostMirrorEditAsync("api/service/v1/faces/training/apply",
            new TrainingArtifactMutation(artifact, epoch, active), database, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new InvalidOperationException("照片空间的训练制品或人物映射已变化，请刷新后重试。");
        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveFaceCandidateAsync(PicerDatabase database, long clusterId, long faceId, CancellationToken cancellationToken = default)
    {
        if (!canWrite) throw new UnauthorizedAccessException("此设备没有编辑照片空间的权限。");
        var epoch = database.GetMirrorCheckpoint()?.Epoch ?? throw new InvalidOperationException("请先刷新照片空间索引。");
        using var response = await PostMirrorEditAsync("api/service/v1/faces/candidate/remove",
            new FaceCandidateRemovalRequest(clusterId, faceId, epoch), database, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new InvalidOperationException("候选分组已变化或正在整理，请刷新后重试。");
        response.EnsureSuccessStatusCode();
    }

    public async Task<FaceOrganizationResult> OrganizeFacesAsync(PicerDatabase database, string modelVersion, string mode,
        CancellationToken cancellationToken = default)
    {
        if (!canWrite) throw new UnauthorizedAccessException("此设备没有编辑照片空间的权限。");
        var epoch = database.GetMirrorCheckpoint()?.Epoch ?? throw new InvalidOperationException("请先刷新照片空间索引。");
        using var response = await PostMirrorEditAsync("api/service/v1/faces/organize",
            new FaceOrganizationRequest(modelVersion, mode, epoch), database, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new InvalidOperationException("照片空间正在整理人物，或索引版本已变化；请稍后刷新重试。");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FaceOrganizationResult>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("face_organization_result_missing");
    }

    public async Task SaveSceneDescriptionAsync(PicerDatabase database,SceneDescriptionMutation request,CancellationToken cancellationToken=default)
    {
        if(!canWrite)throw new UnauthorizedAccessException("此设备没有编辑照片空间的权限。");
        request=request with {ExpectedEpoch=database.GetMirrorCheckpoint()?.Epoch??throw new InvalidOperationException("请先刷新照片空间索引，再编辑场景。")};
        using var response=await PostMirrorEditAsync("api/service/v1/scenes/edit",request,database,cancellationToken).ConfigureAwait(false);
        if(response.StatusCode==System.Net.HttpStatusCode.Conflict)throw new InvalidOperationException("场景内容已在另一端修改，请刷新后重新编辑。");
        response.EnsureSuccessStatusCode();
    }

    public async Task PushFaceReviewDecisionsAsync(PicerDatabase database, CancellationToken cancellationToken = default)
    {
        await _reviewSyncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var reviewDatabase = database.OpenIndependentConnection();
            if (!canWrite || accountMode && reviewDatabase.GetMirrorCheckpoint() is null) return;
            var decisions = reviewDatabase.GetFaceReviewDecisions();
            var fingerprint = Fingerprint(decisions);
            if (fingerprint == _lastReviewFingerprint) return;
            var complete = true;
            foreach (var batch in decisions.Chunk(500))
            {
                using var response = await PostMirrorEditAsync("api/sync/v1/face-review-decisions", batch, reviewDatabase, cancellationToken);
                response.EnsureSuccessStatusCode();
                var accepted = await response.Content.ReadFromJsonAsync<GatewayFaceReviewDecision[]>(Json, cancellationToken)
                    ?? throw new InvalidOperationException("face_review_ack_missing");
                foreach (var mergeBatch in accepted.Chunk(MergeBatchSize))
                    await WriteDatabaseAsync(() => reviewDatabase.MergeFaceReviewDecisions(mergeBatch),
                        cancellationToken).ConfigureAwait(false);
                if (!batch.Select(item => item.FaceId).ToHashSet().SetEquals(accepted.Select(item => item.FaceId))) complete = false;
            }
            // Cache only the sent version, never edits made during the request.
            // The durable journal itself remains until newer explicit decisions
            // supersede it, including after acknowledgment and process restart.
            if (complete) _lastReviewFingerprint = fingerprint;
        }
        finally { _reviewSyncGate.Release(); }
    }

    public async Task PushMetadataEditsAsync(PicerDatabase database, CancellationToken cancellationToken = default)
    {
        await _metadataSyncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var acknowledged = new HashSet<string>(StringComparer.Ordinal);
            using var independent = database.OpenIndependentConnection();
            if (accountMode && independent.GetMirrorCheckpoint() is null) return;
            var edits = accountMode ? independent.GetPendingMetadataEdits() : independent.GetMetadataEdits();
            foreach (var batch in edits.Where(edit => canWrite && (edit.Kind is not ("video-tag-link" or "video-concept")
                || edit.VideoContentIdentity is { Length: 71 } identity && identity.StartsWith("sha256:", StringComparison.Ordinal)
                    && identity[7..].All(Uri.IsHexDigit))).Chunk(500))
            {
                using var response = await PostMirrorEditAsync("api/sync/v1/metadata-edits", batch, independent, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var winners = await response.Content.ReadFromJsonAsync<MetadataEdit[]>(Json, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("metadata_ack_missing");
                await MergeMetadataAsync(independent, winners, cancellationToken).ConfigureAwait(false);
                foreach (var winner in winners) acknowledged.Add(winner.OperationId);
            }
            var remote = await ResilientJsonRead.RunAsync(async token =>
            {
                using var response = await client.GetAsync("api/sync/v1/metadata-edits", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<MetadataEdit[]>(Json, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            await MergeMetadataAsync(independent, remote ?? [], cancellationToken).ConfigureAwait(false);
            foreach (var winner in remote ?? []) acknowledged.Add(winner.OperationId);
            Volatile.Write(ref _acknowledgedMetadataOperations, acknowledged);
        }
        finally { _metadataSyncGate.Release(); }
    }

    private async Task<HttpResponseMessage> PostMirrorEditAsync<T>(string path, T payload, PicerDatabase database, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload, options: Json) };
        if (accountMode)
            request.Headers.Add("X-Picer-Mirror-Epoch", database.GetMirrorCheckpoint()?.Epoch
                ?? throw new InvalidOperationException("remote_baseline_required"));
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public static IndexSyncClient Create(string baseUrl, string token, byte[]? expectedSpkiPin = null,
        Func<Action, CancellationToken, Task>? databaseWork = null)
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
        return new IndexSyncClient(http, databaseWork: databaseWork);
    }

    public async Task<int> PushImagesAsync(PicerDatabase database, IProgress<(int done, int total)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (accountMode) throw new InvalidOperationException("account_source_publisher_required");
        var sources = database.GetSources();
        var total = sources.Sum(source => database.GetImages(source.Id, includeMissing: true).Count);
        var done = 0;
        foreach (var source in sources)
        {
            var images = database.GetImages(source.Id, includeMissing: true);
            foreach (var batch in images.Chunk(200))
            {
                using var response = await client.PostAsJsonAsync("api/sync/v1/images", new { sourcePath = source.Path, images = batch }, Json, cancellationToken);
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
        if (accountMode) throw new InvalidOperationException("account_source_publisher_required");
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

    public async Task PushMetadataAsync(PicerDatabase database, CancellationToken cancellationToken = default,
        bool publishSceneLayouts = true)
    {
        if (accountMode)
        {
            await PushDescriptionsAsync(database, cancellationToken).ConfigureAwait(false);
            await PushMetadataEditsAsync(database, cancellationToken).ConfigureAwait(false);
            await PushFaceReviewDecisionsAsync(database, cancellationToken).ConfigureAwait(false);
            return;
        }
        // Scene ordering is a local index result, not something the remote
        // mirror should recompute. Materialize layouts for every indexed
        // directory before sending metadata; both direct-directory and
        // recursive variants are retained so the remote UI can honor the
        // same classification preference.
        if (publishSceneLayouts)
            await Task.Run(() => EnsureSceneLayoutSnapshots(database, cancellationToken), cancellationToken).ConfigureAwait(false);
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
        await PushMetadataEditsAsync(database, cancellationToken).ConfigureAwait(false);
    }

    public async Task PushDescriptionsAsync(PicerDatabase database, CancellationToken cancellationToken = default)
    {
        if (!accountMode || !canWrite) return;
        await _descriptionSyncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var independent = database.OpenIndependentConnection();
            IReadOnlyList<DescriptionUpload> pending = [];
            await WriteDatabaseAsync(() => pending = independent.GetDescriptionOutbox(pendingOnly: true), cancellationToken).ConfigureAwait(false);
            var conflicts = 0;
            foreach (var item in pending)
            {
                if (item.Epoch != independent.GetMirrorCheckpoint()?.Epoch)
                { conflicts++; continue; }
                using var response = await PostMirrorEditAsync("api/sync/v2/descriptions", item, independent, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.BadRequest)
                { conflicts++; continue; }
                response.EnsureSuccessStatusCode();
                var receipt = await response.Content.ReadFromJsonAsync<DescriptionUploadReceipt>(Json, cancellationToken).ConfigureAwait(false);
                if (receipt?.OperationId != item.OperationId) throw new InvalidDataException("description_ack_missing");
                await WriteDatabaseAsync(() => independent.AcknowledgeDescription(item.OperationId), cancellationToken).ConfigureAwait(false);
            }
            if (conflicts > 0) throw new InvalidOperationException("description_sync_conflicts_pending");
        }
        finally { _descriptionSyncGate.Release(); }
    }

    private static void EnsureSceneLayoutSnapshots(PicerDatabase database, CancellationToken cancellationToken)
    {
        var clusterer = new SceneTimeClusterService(database);
        var descriptions = new SceneGroupDescriptionService(database, new CloudVisionRepository(database));
        foreach (var source in database.GetSources())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var images = database.GetImages(source.Id, includeMissing: false);
            var directories = images.Select(image => Path.GetDirectoryName(image.Path))
                .Where(path => !string.IsNullOrWhiteSpace(path))!
                .Select(path => Path.GetFullPath(path!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var direct = images.Where(image => string.Equals(Path.GetDirectoryName(image.Path), directory, StringComparison.OrdinalIgnoreCase)).ToArray();
                descriptions.SaveLayoutSnapshot(directory, false, clusterer.Build(direct));
                // Recursive snapshots preserve layouts such as 100APPLE→115APPLE
                // when the local directory preference includes descendants.
                descriptions.SaveLayoutSnapshot(directory, true, clusterer.Build(directory, true));
            }
        }
    }

    /// <summary>
    /// Pushes only when the local logical index changed since the last
    /// successful push. The server upserts each entity idempotently, so a
    /// retry or a second client cannot duplicate records.
    /// </summary>
    public async Task<IndexSyncResult> PushIncrementalAsync(PicerDatabase database, string checkpointPath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        bool publishSceneLayouts = true, bool publishMedia = true)
    {
        // An account GUI owns an aggregate read mirror, never a source snapshot.
        // Source uploads use SpaceSyncPublisher with explicit source ownership.
        // Do not even build/fingerprint the legacy full-library payload here.
        if (accountMode)
        {
            string[] pending;
            bool descriptionsPending;
            using (var independent = database.OpenIndependentConnection())
            {
                pending = canWrite ? independent.GetPendingMetadataEdits().Select(edit => edit.OperationId).ToArray() : [];
                descriptionsPending = canWrite && independent.GetDescriptionOutbox(pendingOnly: true).Count > 0;
            }
            await PushMetadataAsync(database, cancellationToken, publishSceneLayouts: false).ConfigureAwait(false);
            return new IndexSyncResult(descriptionsPending || pending.Any(AcknowledgedMetadataOperations.Contains), 0, 0, "", "");
        }
        await PushFaceReviewDecisionsAsync(database, cancellationToken).ConfigureAwait(false);
        await PushMetadataEditsAsync(database, cancellationToken).ConfigureAwait(false);
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
        if (publishMedia && !string.Equals(previous.MediaFingerprint, mediaFingerprint, StringComparison.Ordinal))
        {
            progress?.Report("正在增量同步图片与视频…");
            pushedImages = await PushImagesAsync(database, cancellationToken: cancellationToken).ConfigureAwait(false);
            pushedVideos = await PushVideosAsync(database, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        if (!string.Equals(previous.MetadataFingerprint, metadataFingerprint, StringComparison.Ordinal))
        {
            progress?.Report("正在增量同步人物、标签和分析元数据…");
            await PushMetadataAsync(database, cancellationToken, publishSceneLayouts).ConfigureAwait(false);
            // Scene layout publication materializes snapshots in the local
            // database. Store the post-write fingerprint so an identical
            // subsequent sync is truly idempotent.
            var afterPublish = BuildMetadataRequest(database);
            metadataFingerprint = Fingerprint(afterPublish with
            {
                Faces = afterPublish.Faces?.Select(face => face with { Embedding = null }).ToArray(),
                VideoFaces = afterPublish.VideoFaces?.Select(face => face with { Embedding = null }).ToArray()
            });
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
        // Directory scene expansion is a client view preference. The account
        // service intentionally does not expose this legacy configuration write.
        if (accountMode || !canWrite) return;
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
        var faceTrainingArtifacts = new List<GatewayFaceTrainingArtifact>();
        using (var command = database.Command("SELECT artifact_id,dataset_id,endpoint,model_version,artifact_json,false_accept_rate,false_reject_rate,state,created_utc,applied_utc FROM face_training_artifacts"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) faceTrainingArtifacts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        var facePersonPrototypes = new List<GatewayFacePersonPrototype>();
        using (var command = database.Command("SELECT person_id,centroid,threshold,sample_count,model_version,updated_utc FROM face_person_prototypes"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) facePersonPrototypes.Add(new(reader.GetInt64(0), (byte[])reader[1], reader.GetDouble(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5)));
        var facePersonPolicies = new List<GatewayFacePersonPolicy>();
        using (var command = database.Command("SELECT person_id,artifact_id,model_version,prototype,match_threshold,updated_utc FROM face_person_policies"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) facePersonPolicies.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), (byte[])reader[3], reader.GetDouble(4), reader.GetString(5)));
        var faceModelSettings = new List<GatewayFaceModelSetting>();
        using (var command = database.Command("SELECT model_version,match_threshold,calibrated_same_pairs,calibrated_different_pairs,updated_utc FROM face_model_settings"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) faceModelSettings.Add(new(reader.GetString(0), reader.GetDouble(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4)));
        return new SyncMetadataRequest(
            SemanticEngine.GetTags(database), albums, memberships,
            tagLinks, database.GetImageLocationHierarchyRows(), new PeopleService(database).GetAll(), faces, assignments.Values.ToArray(),
            database.GetAllMirroredVideoKeyframes(), database.GetAllMirroredVideoTranscripts(), database.GetAllMirroredVideoSummaries(), database.GetAllMirroredVideoFaces(),
            database.GetAllMirroredCloudVisionAnalyses(), database.GetAllMirroredSceneGroupDescriptions(),
            database.GetAllMirroredFaceReviewStates(), database.GetAllMirroredFaceCandidateClusters(), database.GetAllMirroredFaceCandidateMembers(),
            faceTrainingArtifacts, facePersonPrototypes, facePersonPolicies, faceModelSettings);
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
        IReadOnlyList<GatewayFaceCandidateMember>? FaceCandidateMembers = null,
        IReadOnlyList<GatewayFaceTrainingArtifact>? FaceTrainingArtifacts = null,
        IReadOnlyList<GatewayFacePersonPrototype>? FacePersonPrototypes = null,
        IReadOnlyList<GatewayFacePersonPolicy>? FacePersonPolicies = null,
        IReadOnlyList<GatewayFaceModelSetting>? FaceModelSettings = null);

}
