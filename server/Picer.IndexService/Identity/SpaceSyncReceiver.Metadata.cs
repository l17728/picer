using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Identity;
using Picer.Core.Search;
using Picer.Core.Video;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncReceiver
{
    private int ApplyMetadataEdits(string publisher, SpaceMetadataEditsBatch batch, Dictionary<(string, long), long> identities, SqliteTransaction transaction)
    {
        if (batch.Edits is null || batch.Edits.Count > 500) throw new ArgumentException("invalid_metadata_edits");
        var edits = batch.Edits.Select(edit =>
        {
            var kind = edit.Kind switch { "person" => "person", "tag-link" => "image", "album-link" => "album", "video-tag-link" or "video-concept" => "video", _ => throw new ArgumentException("invalid_metadata_kind") };
            var field = edit.Kind switch
            {
                "tag-link" or "video-tag-link" => Resolve(publisher, identities, "tag", long.Parse(edit.Field, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "album-link" => Resolve(publisher, identities, "image", long.Parse(edit.Field, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => edit.Field
            };
            return edit with { EntityId = Resolve(publisher, identities, kind, edit.EntityId), Field = field,
                OperationId = DeviceAuthorization.Digest(publisher + ":" + edit.OperationId)[..32] };
        }).ToArray();
        database.MergeMetadataEditsInTransaction(edits, transaction);
        return edits.Length;
    }
    private long Resolve(string publisher, Dictionary<(string, long), long> identities, string kind, long id)
    {
        if (identities.TryGetValue((kind, id), out var mapped)) return mapped;
        var value = Scalar("SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND local_key=$local",
            ("$publisher", publisher), ("$kind", kind), ("$local", id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return value is null ? throw new ArgumentException("unpublished_reference") : Convert.ToInt64(value);
    }

    private void Rebind(string publisher, Dictionary<(string, long), long> identities, string kind, long local, long canonical)
    {
        Execute("UPDATE sync_entity_identities SET target_id=$canonical WHERE publisher_id=$publisher AND kind=$kind AND local_key=$local",
            ("$canonical", canonical), ("$publisher", publisher), ("$kind", kind), ("$local", local.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        identities[(kind, local)] = canonical;
    }

    private int ApplyCatalog(string publisher, SpaceCatalogBatch batch, Dictionary<(string, long), long> identities)
    {
        if (batch.Tags is null || batch.Albums is null || batch.People is null || batch.Tags.Count + batch.Albums.Count + batch.People.Count > 1000) throw new ArgumentException("catalog_batch_too_large");
        foreach (var tag in batch.Tags)
        {
            var local = tag.Id;
            var id = Resolve(publisher, identities, "tag", local);
            var canonical = Scalar("SELECT id FROM tags WHERE name=$name", ("$name", tag.Name));
            if (canonical is not null && Convert.ToInt64(canonical) != id)
            { id = Convert.ToInt64(canonical); Rebind(publisher, identities, "tag", local, id); }
            tag.Id = id;
            database.UpsertMirroredTag(tag);
        }
        foreach (var album in batch.Albums)
        {
            album.Id = Resolve(publisher, identities, "album", album.Id);
            if (album.CreatorId is { } source)
            {
                var target = Scalar("SELECT source_id FROM sync_source_bindings WHERE publisher_id=$publisher AND local_source_id=$source", ("$publisher", publisher), ("$source", source));
                album.CreatorId = target is null ? null : Convert.ToInt64(target);
            }
            database.UpsertMirroredAlbum(album);
        }
        foreach (var person in batch.People)
        {
            var id = Resolve(publisher, identities, "person", person.Id);
            var name = person.DisplayName;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 150) throw new ArgumentException("invalid_person_name");
            // Equal display names are not evidence of equal people. Preserve both until the user explicitly merges them.
            if (Scalar("SELECT id FROM people WHERE trim(display_name)=trim($name) COLLATE NOCASE AND id<>$id", ("$name", name), ("$id", id)) is not null)
                name += "（来源 " + publisher + "）";
            database.UpsertMirroredPerson(person with { Id = id, DisplayName = name });
        }
        return batch.Tags.Count + batch.Albums.Count + batch.People.Count;
    }

    private int ApplyMetadata(string publisher, SpaceMetadataBatch batch, Dictionary<(string, long), long> identities, SqliteTransaction transaction)
    {
        var data = batch.Metadata;
        if (data is null || data.Faces is null || data.FaceAssignments is null || data.AlbumImages is null || data.ImageTags is null ||
            data.Locations is null || data.VideoKeyframes is null || data.VideoTranscripts is null || data.VideoSummaries is null)
            throw new ArgumentException("invalid_metadata");
        var count = data.Faces.Count + data.FaceAssignments.Count + data.AlbumImages.Count + data.ImageTags.Count + data.Locations.Count +
            data.VideoKeyframes.Count + data.VideoFacesOrEmpty.Count + data.CloudVisionAnalysesOrEmpty.Count + data.VideoSummaries.Count +
            data.VideoTranscripts.Count + data.SceneGroupDescriptionsOrEmpty.Count + data.FaceReviewStatesOrEmpty.Count +
            data.FaceCandidateClustersOrEmpty.Count + data.FaceCandidateMembersOrEmpty.Count + data.FaceTrainingArtifactsOrEmpty.Count +
            data.FacePersonPoliciesOrEmpty.Count + data.FacePersonPrototypesOrEmpty.Count + data.FaceModelSettingsOrEmpty.Count + (data.FaceReviewDecisions?.Count ?? 0);
        if (count > 2000) throw new ArgumentException("metadata_batch_too_large");
        long Map(string kind, long id) => Resolve(publisher, identities, kind, id);
        long? OptionalPerson(long? id) => id is null ? null : Map("person", id.Value);
        foreach (var face in data.Faces)
        {
            var image = Map("image", face.ImageId);
            var faceId = Map("face", face.Id);
            var canonical = Scalar("SELECT id FROM detected_faces WHERE image_id=$image AND model_version=$model AND ordinal=$ordinal",
                ("$image", image), ("$model", face.ModelVersion), ("$ordinal", face.Ordinal));
            if (canonical is not null && Convert.ToInt64(canonical) != faceId)
            { faceId = Convert.ToInt64(canonical); Rebind(publisher, identities, "face", face.Id, faceId); }
            database.UpsertMirroredFace(face with { Id = faceId, ImageId = image });
        }
        foreach (var assignment in data.FaceAssignments)
            database.UpsertMirroredFaceAssignment(assignment with { FaceId = Map("face", assignment.FaceId), PersonId = Map("person", assignment.PersonId) });
        foreach (var link in data.AlbumImages)
            Execute("INSERT OR IGNORE INTO album_images(album_id,image_id,added_utc) VALUES($album,$image,$now)",
                ("$album", Map("album", link.AlbumId)), ("$image", Map("image", link.ImageId)), ("$now", DateTime.UtcNow.ToString("O")));
        foreach (var link in data.ImageTags) database.UpsertMirroredImageTag(Map("image", link.ImageId), Map("tag", link.TagId));
        foreach (var location in data.Locations) database.UpsertMirroredLocation(location with { ImageId = Map("image", location.ImageId) });

        var frames = new List<GatewayVideoKeyframe>();
        foreach (var frame in data.VideoKeyframes)
        {
            var video = Map("video", frame.VideoId);
            var target = Map("keyframe", frame.Id);
            var content = new VideoContentRepository(database).GetContentId(video);
            var canonical = Scalar("SELECT id FROM video_keyframes WHERE video_id=$content AND timestamp_seconds=$time ORDER BY id LIMIT 1", ("$content", content), ("$time", frame.TimestampSeconds));
            if (canonical is not null && Convert.ToInt64(canonical) != target)
            { target = Convert.ToInt64(canonical); Rebind(publisher, identities, "keyframe", frame.Id, target); }
            frames.Add(frame with { Id = target, VideoId = video, Path = PublishedMediaPath.Qualify(publisher, frame.Path) });
        }
        string MapImageArray(string json)
        {
            var ids = JsonSerializer.Deserialize<long[]>(json) ?? throw new ArgumentException("invalid_scene_members");
            if (ids.Length > 100_000) throw new ArgumentException("scene_too_large");
            return JsonSerializer.Serialize(ids.Select(id => Map("image", id)));
        }
        var videoFaces = new List<GatewayVideoFace>();
        foreach (var face in data.VideoFacesOrEmpty)
        {
            var video = Map("video", face.VideoId);
            var keyframe = Map("keyframe", face.KeyframeId);
            var target = Map("video-face", face.Id);
            var content = new VideoContentRepository(database).GetContentId(video);
            var canonical = Scalar("SELECT id FROM video_detected_faces WHERE video_id=$content AND keyframe_id=$keyframe AND ordinal=$ordinal AND model_version=$model ORDER BY id LIMIT 1",
                ("$content", content), ("$keyframe", keyframe), ("$ordinal", face.Ordinal), ("$model", face.ModelVersion));
            if (canonical is not null && Convert.ToInt64(canonical) != target)
            { target = Convert.ToInt64(canonical); Rebind(publisher, identities, "video-face", face.Id, target); }
            videoFaces.Add(face with { Id = target, VideoId = video, KeyframeId = keyframe,
                SuggestedPersonId = OptionalPerson(face.SuggestedPersonId), AssignedPersonId = OptionalPerson(face.AssignedPersonId) });
        }
        var media = new GatewayMetadata([], [], [], [], [], frames,
            data.VideoTranscripts.Select(item => item with { VideoId = Map("video", item.VideoId) }).ToArray(),
            data.VideoSummaries.Select(item => item with { VideoId = Map("video", item.VideoId) }).ToArray(),
            videoFaces,
            data.CloudVisionAnalysesOrEmpty.Select(item => item with { ImageId = Map("image", item.ImageId) }).ToArray(),
            data.SceneGroupDescriptionsOrEmpty.Select(item => item with
            {
                Fingerprint = DeviceAuthorization.Digest(publisher + ":" + item.Fingerprint), DirectoryPath = PublishedMediaPath.Qualify(publisher, item.DirectoryPath),
                ImageIdsJson = MapImageArray(item.ImageIdsJson), RepresentativeImageIdsJson = MapImageArray(item.RepresentativeImageIdsJson)
            }).ToArray());
        database.UpsertMirroredVideoMetadata(media, externalTransaction: transaction);
        foreach (var cluster in data.FaceCandidateClustersOrEmpty)
            Execute("INSERT INTO face_candidate_clusters(id,model_version,created_utc) VALUES($id,$model,$created) ON CONFLICT(id) DO UPDATE SET model_version=excluded.model_version",
                ("$id", Map("face-cluster", cluster.Id)), ("$model", cluster.ModelVersion), ("$created", cluster.CreatedUtc));
        foreach (var member in data.FaceCandidateMembersOrEmpty)
            Execute("INSERT INTO face_candidate_members(cluster_id,face_id,similarity) VALUES($cluster,$face,$similarity) ON CONFLICT(face_id) DO UPDATE SET cluster_id=excluded.cluster_id,similarity=excluded.similarity",
                ("$cluster", Map("face-cluster", member.ClusterId)), ("$face", Map("face", member.FaceId)), ("$similarity", member.Similarity));
        var decisions = (data.FaceReviewDecisions ?? []).Select(item => item with { FaceId = Map("face", item.FaceId) })
            .Concat(data.FaceReviewStatesOrEmpty.Select(item => new GatewayFaceReviewDecision(Map("face", item.FaceId), item.State, item.UpdatedUtc))).ToArray();
        database.MergeFaceReviewDecisionsInTransaction(decisions, transaction);
        ApplyTraining(publisher, data, Map);
        database.ReplayMetadataEditsInTransaction(transaction);
        return count;
    }

    private void ApplyTraining(string publisher, GatewayMetadata data, Func<string, long, long> map)
    {
        string Artifact(string id) => publisher + ":" + id;
        foreach (var artifact in data.FaceTrainingArtifactsOrEmpty)
            Execute("""
                INSERT INTO face_training_artifacts(artifact_id,dataset_id,endpoint,model_version,artifact_json,false_accept_rate,false_reject_rate,state,created_utc,applied_utc)
                VALUES($id,$dataset,$endpoint,$model,$json,$far,$frr,$state,$created,$applied)
                ON CONFLICT(artifact_id) DO UPDATE SET artifact_json=excluded.artifact_json,false_accept_rate=excluded.false_accept_rate,
                false_reject_rate=excluded.false_reject_rate,state=excluded.state,applied_utc=excluded.applied_utc
                """, ("$id", Artifact(artifact.ArtifactId)), ("$dataset", Artifact(artifact.DatasetId)), ("$endpoint", "source:" + publisher),
                ("$model", artifact.ModelVersion), ("$json", artifact.ArtifactJson), ("$far", artifact.FalseAcceptRate), ("$frr", artifact.FalseRejectRate),
                ("$state", artifact.State), ("$created", artifact.CreatedUtc), ("$applied", artifact.AppliedUtc));
        foreach (var prototype in data.FacePersonPrototypesOrEmpty)
            Execute("""
                INSERT INTO face_person_prototypes(person_id,centroid,threshold,sample_count,model_version,updated_utc) VALUES($person,$centroid,$threshold,$count,$model,$updated)
                ON CONFLICT(person_id,model_version) DO UPDATE SET centroid=excluded.centroid,threshold=excluded.threshold,sample_count=excluded.sample_count,updated_utc=excluded.updated_utc
                WHERE excluded.updated_utc>=face_person_prototypes.updated_utc
                """, ("$person", map("person", prototype.PersonId)), ("$centroid", prototype.Centroid), ("$threshold", prototype.Threshold),
                ("$count", prototype.SampleCount), ("$model", prototype.ModelVersion), ("$updated", prototype.UpdatedUtc));
        foreach (var policy in data.FacePersonPoliciesOrEmpty)
            Execute("""
                INSERT INTO face_person_policies(person_id,artifact_id,model_version,prototype,match_threshold,updated_utc) VALUES($person,$artifact,$model,$prototype,$threshold,$updated)
                ON CONFLICT(person_id) DO UPDATE SET artifact_id=excluded.artifact_id,model_version=excluded.model_version,prototype=excluded.prototype,match_threshold=excluded.match_threshold,updated_utc=excluded.updated_utc
                WHERE excluded.updated_utc>=face_person_policies.updated_utc
                """, ("$person", map("person", policy.PersonId)), ("$artifact", Artifact(policy.ArtifactId)), ("$model", policy.ModelVersion),
                ("$prototype", policy.Prototype), ("$threshold", policy.MatchThreshold), ("$updated", policy.UpdatedUtc));
        foreach (var setting in data.FaceModelSettingsOrEmpty)
            Execute("""
                INSERT INTO face_model_settings(model_version,match_threshold,calibrated_same_pairs,calibrated_different_pairs,updated_utc) VALUES($model,$threshold,$same,$different,$updated)
                ON CONFLICT(model_version) DO NOTHING
                """, ("$model", setting.ModelVersion), ("$threshold", setting.MatchThreshold), ("$same", setting.CalibratedSamePairs),
                ("$different", setting.CalibratedDifferentPairs), ("$updated", setting.UpdatedUtc));
    }
}
