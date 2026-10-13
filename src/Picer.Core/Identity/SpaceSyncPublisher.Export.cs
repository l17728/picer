using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.People;
using Picer.Core.Search;
using Picer.Core.Video;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    private static IEnumerable<ExportBatch> ExportBatches(PicerDatabase snapshot, bool includeMetadata)
    {
        var sources = snapshot.GetSources().OrderBy(source => source.Id).ToArray();
        if (includeMetadata)
        {
        foreach (var source in sources) yield return new("sources", new SpaceSourceBatch(source.Id, source.Path), []);
        foreach (var tags in SemanticEngine.GetTags(snapshot).OrderBy(tag => tag.Id).Chunk(200))
            yield return new("catalog", new SpaceCatalogBatch(tags, [], []), tags.Select(tag => ("tag", tag.Id)).ToArray());
        foreach (var albums in SemanticEngine.GetAlbums(snapshot).OrderBy(album => album.Id).Chunk(200))
            yield return new("catalog", new SpaceCatalogBatch([], albums, []), albums.Select(album => ("album", album.Id)).ToArray());
        foreach (var people in new PeopleService(snapshot).GetAll().OrderBy(person => person.Id).Chunk(200))
            yield return new("catalog", new SpaceCatalogBatch([], [], people), people.Select(person => ("person", person.Id)).ToArray());
        }
        foreach (var source in sources)
        foreach (var images in snapshot.EnumerateUploadImages(source.Id).Chunk(200))
            yield return new("images", new SpaceImageBatch(source.Id, source.Path, images), images.Select(image => ("image", image.Id)).ToArray());
        foreach (var source in sources)
        foreach (var batch in snapshot.EnumerateUploadVideos(source.Id).Chunk(100))
            yield return new("videos", new SpaceVideoBatch(source.Id, source.Path, batch), batch.Select(video => ("video", video.Id)).ToArray());

        if (!includeMetadata) yield break;

        foreach (var faces in snapshot.EnumerateMirroredFaces().Chunk(400))
            yield return Metadata(Empty() with { Faces = faces }, faces.Select(face => ("face", face.Id)).ToArray());
        foreach (var assignments in snapshot.GetAllMirroredFaceAssignments().OrderBy(item => item.FaceId).Chunk(400))
            yield return Metadata(Empty() with { FaceAssignments = assignments });
        foreach (var batch in Read(snapshot, "SELECT album_id,image_id FROM album_images ORDER BY album_id,image_id", row => new GatewayAlbumImageLink(row.GetInt64(0), row.GetInt64(1))).Chunk(500))
            yield return Metadata(Empty() with { AlbumImages = batch });
        foreach (var batch in Read(snapshot, "SELECT image_id,tag_id FROM image_tags ORDER BY image_id,tag_id", row => new GatewayImageTagLink(row.GetInt64(0), row.GetInt64(1))).Chunk(500))
            yield return Metadata(Empty() with { ImageTags = batch });
        foreach (var batch in snapshot.GetImageLocationHierarchyRows().OrderBy(row => row.ImageId).Chunk(400))
            yield return Metadata(Empty() with { Locations = batch });
        foreach (var batch in snapshot.GetAllMirroredVideoKeyframes().OrderBy(frame => frame.Id).Chunk(200))
            yield return Metadata(Empty() with { VideoKeyframes = batch }, batch.Select(frame => ("keyframe", frame.Id)).ToArray());
        foreach (var batch in snapshot.GetAllMirroredVideoFaces().OrderBy(face => face.Id).Chunk(300))
            yield return Metadata(Empty() with { VideoFaces = batch }, batch.Select(face => ("video-face", face.Id)).ToArray());
        foreach (var batch in snapshot.GetAllMirroredVideoTranscripts().OrderBy(item => item.VideoId).Chunk(20))
            yield return Metadata(Empty() with { VideoTranscripts = batch });
        foreach (var batch in snapshot.GetAllMirroredVideoSummaries().OrderBy(item => item.VideoId).Chunk(100))
            yield return Metadata(Empty() with { VideoSummaries = batch });
        foreach (var batch in snapshot.GetAllMirroredCloudVisionAnalyses().OrderBy(item => item.ImageId).ThenBy(item => item.Provider, StringComparer.Ordinal).Chunk(100))
            yield return Metadata(Empty() with { CloudVisionAnalyses = batch });
        foreach (var batch in snapshot.GetAllMirroredSceneGroupDescriptions().OrderBy(item => item.Fingerprint, StringComparer.Ordinal).Chunk(20))
            yield return Metadata(Empty() with { SceneGroupDescriptions = batch });
        foreach (var batch in snapshot.GetAllMirroredFaceCandidateClusters().OrderBy(item => item.Id).Chunk(200))
            yield return Metadata(Empty() with { FaceCandidateClusters = batch }, batch.Select(item => ("face-cluster", item.Id)).ToArray());
        foreach (var batch in snapshot.GetAllMirroredFaceCandidateMembers().OrderBy(item => item.ClusterId).ThenBy(item => item.FaceId).Chunk(400))
            yield return Metadata(Empty() with { FaceCandidateMembers = batch });
        foreach (var batch in snapshot.GetFaceReviewDecisions().OrderBy(item => item.FaceId).Chunk(400))
            yield return Metadata(Empty() with { FaceReviewDecisions = batch });
        foreach (var batch in snapshot.GetAllMirroredFaceReviewStates().OrderBy(item => item.FaceId).Chunk(400))
            yield return Metadata(Empty() with { FaceReviewStates = batch });

        foreach (var batch in Read(snapshot,
            "SELECT artifact_id,dataset_id,endpoint,model_version,artifact_json,false_accept_rate,false_reject_rate,state,created_utc,applied_utc FROM face_training_artifacts ORDER BY artifact_id",
            row => new GatewayFaceTrainingArtifact(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4), row.GetDouble(5), row.GetDouble(6), row.GetString(7), row.GetString(8), row.IsDBNull(9) ? null : row.GetString(9))).Chunk(20))
            yield return Metadata(Empty() with { FaceTrainingArtifacts = batch });
        foreach (var batch in Read(snapshot,
            "SELECT person_id,centroid,threshold,sample_count,model_version,updated_utc FROM face_person_prototypes ORDER BY person_id,model_version",
            row => new GatewayFacePersonPrototype(row.GetInt64(0), (byte[])row[1], row.GetDouble(2), row.GetInt32(3), row.GetString(4), row.GetString(5))).Chunk(200))
            yield return Metadata(Empty() with { FacePersonPrototypes = batch });
        foreach (var batch in Read(snapshot,
            "SELECT person_id,artifact_id,model_version,prototype,match_threshold,updated_utc FROM face_person_policies ORDER BY person_id",
            row => new GatewayFacePersonPolicy(row.GetInt64(0), row.GetString(1), row.GetString(2), (byte[])row[3], row.GetDouble(4), row.GetString(5))).Chunk(200))
            yield return Metadata(Empty() with { FacePersonPolicies = batch });
        foreach (var batch in Read(snapshot,
            "SELECT model_version,match_threshold,calibrated_same_pairs,calibrated_different_pairs,updated_utc FROM face_model_settings ORDER BY model_version",
            row => new GatewayFaceModelSetting(row.GetString(0), row.GetDouble(1), row.GetInt32(2), row.GetInt32(3), row.GetString(4))).Chunk(200))
            yield return Metadata(Empty() with { FaceModelSettings = batch });
        foreach (var batch in snapshot.GetMetadataEdits().OrderBy(edit => edit.Kind, StringComparer.Ordinal).ThenBy(edit => edit.EntityId).ThenBy(edit => edit.Field, StringComparer.Ordinal).Chunk(400))
            yield return new("metadata-edits", new SpaceMetadataEditsBatch(batch), []);
    }

    private static GatewayMetadata Empty() => new([], [], []);
    private static ExportBatch Metadata(GatewayMetadata metadata, IReadOnlyList<(string Kind, long Id)>? identities = null) => new("metadata", new SpaceMetadataBatch(metadata), identities ?? []);
    private static IEnumerable<T> Read<T>(PicerDatabase database, string sql, Func<SqliteDataReader, T> decode)
    {
        using var command = database.Command(sql);
        using var row = command.ExecuteReader();
        while (row.Read()) yield return decode(row);
    }
}
