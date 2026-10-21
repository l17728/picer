using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.SemanticSearch;

public sealed record ClipSearchHit(ImageRecord Image, float Score, int Rank);
public sealed record ClipSearchReport(IReadOnlyList<ClipSearchHit> Hits, int Indexed, int Eligible,
    int InvalidVectors);
public sealed record ClipIndexProgress(int Completed, int Total, string CurrentFile);

public sealed class ClipSemanticRepository(PicerDatabase db)
{
    public bool IsCurrent(ImageRecord image, string modelVersion)
    {
        using var command = db.Command("SELECT COUNT(*) FROM clip_image_embeddings WHERE image_id=$id AND model_version=$model AND source_file_size=$size AND source_modified_utc=$modified");
        command.Parameters.AddWithValue("$id", image.Id);
        command.Parameters.AddWithValue("$model", modelVersion);
        command.Parameters.AddWithValue("$size", image.FileSize);
        command.Parameters.AddWithValue("$modified", image.ModifiedUtc.ToString("O"));
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    public void Upsert(ImageRecord image, string modelVersion, ReadOnlySpan<float> embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding.ToArray(), 0, bytes, 0, bytes.Length);
        using var command = db.Command("""
            INSERT INTO clip_image_embeddings(image_id,model_version,source_file_size,source_modified_utc,embedding,indexed_utc)
            VALUES($id,$model,$size,$modified,$embedding,$now)
            ON CONFLICT(image_id) DO UPDATE SET model_version=excluded.model_version,source_file_size=excluded.source_file_size,
            source_modified_utc=excluded.source_modified_utc,embedding=excluded.embedding,indexed_utc=excluded.indexed_utc
            """);
        command.Parameters.AddWithValue("$id", image.Id);
        command.Parameters.AddWithValue("$model", modelVersion);
        command.Parameters.AddWithValue("$size", image.FileSize);
        command.Parameters.AddWithValue("$modified", image.ModifiedUtc.ToString("O"));
        command.Parameters.AddWithValue("$embedding", bytes);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ClipSearchHit> Search(ReadOnlySpan<float> query, int count = 20, string modelVersion = ChineseClipEncoder.ModelVersion)
        => SearchStreaming(query, count, modelVersion).Hits;

    public ClipSearchReport SearchStreaming(ReadOnlySpan<float> query, int count = 20,
        string modelVersion = ChineseClipEncoder.ModelVersion, IReadOnlySet<long>? allowedImageIds = null,
        int cancellationBatchSize = 256, CancellationToken cancellationToken = default)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        if (cancellationBatchSize < 1) throw new ArgumentOutOfRangeException(nameof(cancellationBatchSize));
        var candidates = new PriorityQueue<(long Id, float Score), (float Score, long Tie)>();
        var indexed = 0;
        var eligible = 0;
        var invalid = 0;
        using var command = db.Command("SELECT image_id,embedding FROM clip_image_embeddings WHERE model_version=$model");
        command.Parameters.AddWithValue("$model", modelVersion);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (indexed++ % cancellationBatchSize == 0) cancellationToken.ThrowIfCancellationRequested();
            var id = reader.GetInt64(0);
            if (allowedImageIds is not null && !allowedImageIds.Contains(id)) continue;
            eligible++;
            var bytes = (byte[])reader[1];
            if (bytes.Length != query.Length * sizeof(float)) { invalid++; continue; }
            var score = 0f;
            for (var i = 0; i < query.Length; i++) score += BitConverter.ToSingle(bytes, i * sizeof(float)) * query[i];
            candidates.Enqueue((id, score), (score, -id));
            if (candidates.Count > count) candidates.Dequeue();
        }
        var hits = candidates.UnorderedItems.Select(item => item.Element).OrderByDescending(x => x.Score).ThenBy(x => x.Id)
            .Select((x, index) => db.GetImage(x.Id) is { } image ? new ClipSearchHit(image, x.Score, index + 1) : null)
            .Where(x => x is not null).Cast<ClipSearchHit>().ToArray();
        return new ClipSearchReport(hits, indexed, eligible, invalid);
    }

    public long SaveEvaluation(string query, string modelVersion, int? expectedRelevantCount,
        IEnumerable<(ClipSearchHit Hit, bool? Relevant)> results)
    {
        using var insert = db.Command("INSERT INTO clip_query_evaluations(query,model_version,expected_relevant_count,created_utc) VALUES($q,$m,$e,$now); SELECT last_insert_rowid();");
        insert.Parameters.AddWithValue("$q", query);
        insert.Parameters.AddWithValue("$m", modelVersion);
        insert.Parameters.AddWithValue("$e", expectedRelevantCount is null ? DBNull.Value : expectedRelevantCount.Value);
        insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var id = Convert.ToInt64(insert.ExecuteScalar());
        foreach (var result in results)
        {
            using var row = db.Command("INSERT INTO clip_query_results(evaluation_id,image_id,rank,score,relevant) VALUES($e,$i,$r,$s,$v)");
            row.Parameters.AddWithValue("$e", id); row.Parameters.AddWithValue("$i", result.Hit.Image.Id);
            row.Parameters.AddWithValue("$r", result.Hit.Rank); row.Parameters.AddWithValue("$s", result.Hit.Score);
            row.Parameters.AddWithValue("$v", result.Relevant is null ? DBNull.Value : result.Relevant.Value ? 1 : 0);
            row.ExecuteNonQuery();
        }
        return id;
    }
}
