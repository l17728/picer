using Picer.Core.Data;

namespace Picer.Core.Search;

public sealed class MediaTextRanking(PicerDatabase database)
{
    public IReadOnlyDictionary<long, double> Search(IEnumerable<string> terms, bool requireAll = false)
    {
        var query = ChineseSearchTokens.Query(terms, requireAll);
        if (query.Length == 0) return new Dictionary<long, double>();
        using var command = database.Command("""
            SELECT rowid, bm25(media_text_rank_fts) FROM media_text_rank_fts
            WHERE media_text_rank_fts MATCH $query ORDER BY bm25(media_text_rank_fts), rowid
            """);
        command.Parameters.AddWithValue("$query", query);
        using var reader = command.ExecuteReader();
        var scores = new Dictionary<long, double>();
        while (reader.Read()) scores[reader.GetInt64(0)] = -reader.GetDouble(1);
        reader.Close();
        var videoScores = scores.Where(item => item.Key < 0).ToDictionary(item => -item.Key, item => item.Value);
        foreach (var key in scores.Keys.Where(key => key < 0).ToArray()) scores.Remove(key);
        if (videoScores.Count > 0)
        {
            using var references = database.Command("SELECT id,content_id FROM videos WHERE status='active'");
            using var videos = references.ExecuteReader();
            while (videos.Read())
                if (videoScores.TryGetValue(videos.GetInt64(1), out var score)) scores[-videos.GetInt64(0)] = score;
        }
        using var shared = database.Command("""
            SELECT i.id, ranked.score FROM (
                SELECT rowid, bm25(content_text_fts) AS score FROM content_text_fts
                WHERE content_text_fts MATCH $query
            ) ranked JOIN content_text_documents d ON d.id=ranked.rowid
              JOIN images i ON COALESCE(i.content_sha256,i.sha256)=d.content_sha256
            """);
        shared.Parameters.AddWithValue("$query", query);
        using var contentReader = shared.ExecuteReader();
        while (contentReader.Read()) scores[contentReader.GetInt64(0)] = -contentReader.GetDouble(1);
        return scores;
    }
}
