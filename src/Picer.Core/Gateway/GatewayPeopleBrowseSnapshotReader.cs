using Picer.Core.Data;
using Picer.Core.People;
using Picer.Core.Vision;
using Picer.Core.Video;

namespace Picer.Core.Gateway;

public static class GatewayPeopleBrowseSnapshotReader
{
    public static GatewayPeopleBrowseSnapshot Read(PicerDatabase database, string view, string? modelVersion,
        long? personId, int limit)
    {
        var repository = new VisionAnalysisRepository(database);
        var normalizedView = view.Trim().ToLowerInvariant();
        var boundedLimit = Math.Clamp(limit, 1, 5000);
        IReadOnlyList<UnassignedFaceRecord> faces = normalizedView switch
        {
            "pending" => repository.GetUnassignedFaces(boundedLimit, modelVersion),
            "ignored" => repository.GetIgnoredFaces(boundedLimit, modelVersion),
            "person" when personId is > 0 => repository.GetPersonFaces(personId.Value, boundedLimit),
            "summary" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, "Unsupported people browse view.")
        };

        var includeWorkflowLists = normalizedView is "pending" or "summary";
        var includeCounts = normalizedView is "pending" or "summary";
        return new GatewayPeopleBrowseSnapshot(
            new PeopleService(database).GetAll(),
            faces,
            includeWorkflowLists ? repository.GetCandidateClusters(modelVersion) : [],
            includeWorkflowLists ? repository.GetSuggestedPersonGroups(modelVersion) : [],
            includeCounts ? repository.GetFaceWorkQueueCounts(modelVersion) : new FaceWorkQueueCounts(0, 0, 0, 0),
            includeCounts && !string.IsNullOrWhiteSpace(modelVersion)
                ? repository.GetFacePartitionCounts(modelVersion)
                : new FacePartitionCounts(0, 0, 0, 0, 0, 0),
            includeCounts ? repository.GetIgnoredFaceCount() : 0,
            includeCounts ? repository.GetSummary() : new VisionAnalysisSummary(0, 0, 0, 0),
            normalizedView == "summary" && !string.IsNullOrWhiteSpace(modelVersion)
                ? repository.GetFaceTrainingHistory(modelVersion)
                : [],
            normalizedView == "person" && personId is > 0
                ? ReadPersonVideoFaces(database, personId.Value, boundedLimit)
                : []);
    }

    private static IReadOnlyList<GatewayPersonVideoFace> ReadPersonVideoFaces(PicerDatabase database, long personId, int limit)
    {
        var rows = new List<(long VideoId, long FaceId, double Timestamp, float X, float Y, float Width, float Height,
            float Confidence, long? SuggestedPersonId, string? SuggestedPersonName, double? MatchConfidence,
            long? AssignedPersonId, string? AssignedPersonName, string? AssignmentState, string ModelVersion, long KeyframeId)>();
        using (var command = database.Command("""
            SELECT v.id,vf.id,vf.timestamp_seconds,vf.box_x,vf.box_y,vf.box_width,vf.box_height,vf.confidence,
                   vf.suggested_person_id,sp.display_name,vf.match_confidence,vf.assigned_person_id,ap.display_name,
                   vf.assignment_state,vf.model_version,vf.keyframe_id
            FROM video_detected_faces vf
            JOIN video_effective_faces owned ON owned.id=vf.id
            JOIN videos v ON v.id=owned.reference_id AND v.status='active'
            LEFT JOIN people sp ON sp.id=vf.suggested_person_id
            LEFT JOIN people ap ON ap.id=vf.assigned_person_id
            WHERE vf.assigned_person_id=$person AND vf.assignment_state='confirmed'
            ORDER BY v.id,vf.timestamp_seconds,vf.id
            LIMIT $limit
            """))
        {
            command.Parameters.AddWithValue("$person", personId);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetDouble(2), reader.GetFloat(3), reader.GetFloat(4),
                    reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7), reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    reader.IsDBNull(11) ? null : reader.GetInt64(11), reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetString(14), reader.GetInt64(15)));
            }
        }

        var repository = new VideoRepository(database);
        var videoCache = new Dictionary<long, VideoRecord?>();
        var frameCache = new Dictionary<long, IReadOnlyList<VideoKeyframe>>();
        var result = new List<GatewayPersonVideoFace>(rows.Count);
        foreach (var row in rows)
        {
            if (!videoCache.TryGetValue(row.VideoId, out var video))
                videoCache[row.VideoId] = video = repository.Get(row.VideoId);
            if (video is null) continue;
            if (!frameCache.TryGetValue(row.VideoId, out var frames))
                frameCache[row.VideoId] = frames = repository.GetKeyframes(row.VideoId, existingOnly: false);
            var ordinal = -1;
            for (var i = 0; i < frames.Count; i++)
                if (frames[i].Id == row.KeyframeId) { ordinal = i; break; }
            if (ordinal < 0) continue;
            var face = new VideoFaceTimelineRecord(row.Timestamp, frames[ordinal].Path,
                new(row.X, row.Y, row.Width, row.Height), row.Confidence, row.FaceId, row.SuggestedPersonId,
                row.SuggestedPersonName, row.MatchConfidence, row.AssignedPersonId, row.AssignedPersonName, row.AssignmentState)
            { ModelVersion = row.ModelVersion };
            result.Add(new(face, video, frames[ordinal], ordinal));
        }
        return result;
    }
}
