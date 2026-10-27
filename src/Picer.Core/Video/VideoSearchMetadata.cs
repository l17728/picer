using Picer.Core.Data;
using Picer.Core.Search;

namespace Picer.Core.Video;

public sealed class VideoSearchMetadata(PicerDatabase database)
{
    public IReadOnlySet<long> Match(IReadOnlyList<long> people, IReadOnlyList<long> tags, IReadOnlyList<string> concepts)
    {
        var clauses = new List<string>();
        using var command = database.Command("");
        for (var i = 0; i < people.Count; i++)
        {
            clauses.Add($"EXISTS(SELECT 1 FROM video_effective_faces f WHERE f.reference_id=v.id AND f.assigned_person_id=$p{i} AND f.assignment_state='confirmed')");
            command.Parameters.AddWithValue("$p" + i, people[i]);
        }
        for (var i = 0; i < tags.Count; i++)
        {
            clauses.Add($"EXISTS(SELECT 1 FROM video_effective_tags t WHERE t.video_id=v.id AND t.tag_id=$t{i})");
            command.Parameters.AddWithValue("$t" + i, tags[i]);
        }
        for (var i = 0; i < concepts.Count; i++)
        {
            clauses.Add($"EXISTS(SELECT 1 FROM video_effective_concepts c WHERE c.video_id=v.id AND c.concept_id=$c{i} AND c.state IN ('accepted','suggested')) AND NOT EXISTS(SELECT 1 FROM video_effective_concepts r WHERE r.video_id=v.id AND r.concept_id=$c{i} AND r.state='rejected' AND r.model_version='manual')");
            command.Parameters.AddWithValue("$c" + i, concepts[i]);
        }
        command.CommandText = "SELECT v.id FROM videos v" + (clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses));
        using var reader = command.ExecuteReader();
        var ids = new HashSet<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    public void SetTag(long videoId, long tagId, bool assigned)
    {
        using var transaction = database.BeginTransaction();
        using var command = database.Command(assigned
            ? "INSERT OR IGNORE INTO video_tags(video_id,tag_id) VALUES($video,$tag)"
            : "DELETE FROM video_tags WHERE video_id=$video AND tag_id=$tag");
        command.Parameters.AddWithValue("$video", new VideoContentRepository(database).GetContentId(videoId));
        command.Parameters.AddWithValue("$tag", tagId);
        command.Transaction = transaction;
        if (database.CanWriteLegacyVideoMetadata(videoId,transaction)) command.ExecuteNonQuery();
        database.RecordMetadataEdit("video-tag-link", videoId, tagId.ToString(System.Globalization.CultureInfo.InvariantCulture), assigned ? "1" : "0", transaction);
        transaction.Commit();
    }

    public void SetConcept(long videoId, string conceptId, string modelVersion, string state = "accepted", Microsoft.Data.Sqlite.SqliteTransaction? transaction = null)
    {
        if (string.IsNullOrWhiteSpace(conceptId) || string.IsNullOrWhiteSpace(modelVersion)) throw new ArgumentException("概念与模型版本不能为空");
        if (state is not ("accepted" or "suggested" or "rejected")) throw new ArgumentOutOfRangeException(nameof(state));
        using var ownedTransaction = transaction is null ? database.BeginTransaction() : null;
        transaction ??= ownedTransaction!;
        using var command = database.Command("""
            INSERT INTO video_visual_concepts(video_id,concept_id,model_version,state) VALUES($video,$concept,$model,$state)
            ON CONFLICT(video_id,concept_id,model_version) DO UPDATE SET state=excluded.state
            """);
        command.Parameters.AddWithValue("$video", new VideoContentRepository(database).GetContentId(videoId));
        command.Parameters.AddWithValue("$concept", conceptId);
        command.Parameters.AddWithValue("$model", modelVersion);
        command.Parameters.AddWithValue("$state", state);
        command.Transaction = transaction;
        if (database.CanWriteLegacyVideoMetadata(videoId,transaction)) command.ExecuteNonQuery();
        database.RecordMetadataEdit("video-concept", videoId, System.Text.Json.JsonSerializer.Serialize(new[] { conceptId, modelVersion }), state, transaction);
        ownedTransaction?.Commit();
    }

    public IReadOnlySet<long> GetTags(long videoId)
    {
        using var command = database.Command("SELECT tag_id FROM video_effective_tags WHERE video_id=$id");
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        var result = new HashSet<long>();
        while (reader.Read()) result.Add(reader.GetInt64(0));
        return result;
    }

    public IReadOnlySet<string> GetConcepts(long videoId)
    {
        using var command = database.Command("SELECT DISTINCT concept_id FROM video_effective_concepts c WHERE video_id=$id AND state IN ('accepted','suggested') AND NOT EXISTS(SELECT 1 FROM video_effective_concepts r WHERE r.video_id=c.video_id AND r.concept_id=c.concept_id AND r.model_version='manual' AND r.state='rejected')");
        command.Parameters.AddWithValue("$id", videoId);
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public static bool Within(VideoRecord video, GeoBounds? bounds) => bounds is null ||
        GeoPlaceResolver.IsWithin(video.Gps, bounds.MinLatitude, bounds.MaxLatitude, bounds.MinLongitude, bounds.MaxLongitude);
}
