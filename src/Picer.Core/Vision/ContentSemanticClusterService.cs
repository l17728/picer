using System.Security.Cryptography;
using System.Text;
using Picer.Core.Data;

namespace Picer.Core.Vision;

public sealed record ContentClusterResult(int Considered, int ClusterCount, int ClusteredImages);
public sealed record ContentClusterSettings(bool Automatic, float ManualThreshold, float RecommendedThreshold,
    float EffectiveThreshold);
public sealed record ContentClusterPreview(float Threshold, int EstimatedClusters, int EstimatedClusteredImages);

/// <summary>Builds deterministic, multi-label-aware visual groups from the persisted CLIP index.</summary>
public sealed class ContentSemanticClusterService(PicerDatabase database)
{
    private sealed record Item(long ImageId, float[] Vector, string[] Concepts, string? Summary)
    {
        public string Signature => string.Join('|', Concepts.Order(StringComparer.Ordinal).Take(3));
    }
    private sealed class Draft(Item seed, string? existingKey = null)
    {
        public string? ExistingKey { get; } = existingKey;
        public string Signature { get; } = seed.Signature;
        public List<Item> Members { get; } = [seed];
        public float[] Centroid { get; } = (float[])seed.Vector.Clone();
        public void Add(Item item)
        {
            Members.Add(item);
            for (var i = 0; i < Centroid.Length; i++) Centroid[i] += (item.Vector[i] - Centroid[i]) / Members.Count;
            Normalize(Centroid);
        }
    }

    public ContentClusterResult Rebuild(string modelVersion, float? similarityThreshold = null, int minimumSize = 3)
    {
        var effectiveThreshold = similarityThreshold ?? GetSettings().EffectiveThreshold;
        var items = Load(modelVersion);
        var existing = LoadExistingAssignments();
        var drafts = new List<Draft>();
        foreach (var bucket in items.Where(item => item.Concepts.Length > 0)
                     .GroupBy(item => item.Signature))
        {
            var values = bucket.OrderBy(value => value.ImageId).ToArray();
            var local = values.Where(item => existing.TryGetValue(item.ImageId, out var prior) && prior.Signature == item.Signature)
                .GroupBy(item => existing[item.ImageId].ClusterKey)
                .Select(group =>
                {
                    var members = group.ToArray(); var draft = new Draft(members[0], group.Key);
                    foreach (var member in members.Skip(1)) draft.Add(member);
                    return draft;
                }).ToList();
            var retained = local.SelectMany(cluster => cluster.Members).Select(item => item.ImageId).ToHashSet();
            foreach (var item in values.Where(item => !retained.Contains(item.ImageId)))
            {
                var best = local.Select(cluster => (Cluster: cluster, Score: Cosine(item.Vector, cluster.Centroid)))
                    .OrderByDescending(value => value.Score).FirstOrDefault();
                if (best.Cluster is not null && best.Score >= effectiveThreshold) best.Cluster.Add(item);
                else local.Add(new Draft(item));
            }
            drafts.AddRange(local.Where(cluster => cluster.ExistingKey is not null || cluster.Members.Count >= minimumSize));
        }
        Save(modelVersion, drafts);
        return new(items.Count, drafts.Count, drafts.Sum(cluster => cluster.Members.Count));
    }

    private Dictionary<long, (string ClusterKey, string Signature)> LoadExistingAssignments()
    {
        using var command = database.Command("SELECT image_id,cluster_key,semantic_signature FROM semantic_content_cluster_members");
        using var reader = command.ExecuteReader(); var result = new Dictionary<long, (string, string)>();
        while (reader.Read()) result[reader.GetInt64(0)] = (reader.GetString(1), reader.GetString(2));
        return result;
    }

    public int RefreshLabelsIncremental()
    {
        using var find = database.Command("""
            SELECT c.cluster_key FROM semantic_content_clusters c
            WHERE EXISTS (
                SELECT 1 FROM semantic_content_cluster_members m
                JOIN cloud_vision_analyses v ON v.image_id=m.image_id AND v.status='completed'
                WHERE m.cluster_key=c.cluster_key AND v.updated_utc>c.updated_utc)
            """);
        using var reader = find.ExecuteReader(); var keys = new List<string>();
        while (reader.Read()) keys.Add(reader.GetString(0)); reader.Close();
        foreach (var key in keys)
        {
            var label = BuildStoredClusterLabel(key);
            using var update = database.Command("UPDATE semantic_content_clusters SET display_name=$label,updated_utc=$now WHERE cluster_key=$key");
            update.Parameters.AddWithValue("$label", label); update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$key", key); update.ExecuteNonQuery();
        }
        return keys.Count;
    }

    private List<Item> Load(string modelVersion)
    {
        using var command = database.Command("SELECT e.image_id,e.embedding FROM clip_image_embeddings e JOIN images i ON i.id=e.image_id WHERE e.model_version=$model AND i.status='active' ORDER BY e.image_id");
        command.Parameters.AddWithValue("$model", modelVersion);
        using var reader = command.ExecuteReader();
        var rows = new List<(long Id, float[] Vector)>();
        while (reader.Read())
        {
            var bytes = (byte[])reader[1];
            if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0) continue;
            var vector = new float[bytes.Length / sizeof(float)]; Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length); Normalize(vector);
            rows.Add((reader.GetInt64(0), vector));
        }
        reader.Close();
        var concepts = new Dictionary<long, List<string>>();
        using (var conceptCommand = database.Command("""
            SELECT image_id,concept_id FROM image_classification_assertions
            WHERE source_kind='vision_model' AND review_state IN ('pending','accepted') AND concept_id NOT LIKE 'cluster.%'
            ORDER BY image_id,confidence DESC
            """))
        using (var conceptReader = conceptCommand.ExecuteReader())
            while (conceptReader.Read())
            {
                var id = conceptReader.GetInt64(0);
                if (!concepts.TryGetValue(id, out var values)) concepts[id] = values = [];
                if (values.Count < 6) values.Add(conceptReader.GetString(1));
            }
        var summaries = new Dictionary<long, string>();
        using (var summaryCommand = database.Command("""
            SELECT image_id,summary FROM cloud_vision_analyses
            WHERE status='completed' AND length(trim(summary))>0 ORDER BY image_id,updated_utc
            """))
        using (var summaryReader = summaryCommand.ExecuteReader())
            while (summaryReader.Read()) summaries[summaryReader.GetInt64(0)] = summaryReader.GetString(1);
        return rows.Select(row => new Item(row.Id, row.Vector,
            concepts.TryGetValue(row.Id, out var labels) ? labels.ToArray() : [],
            summaries.GetValueOrDefault(row.Id))).ToList();
    }

    private string BuildStoredClusterLabel(string key)
    {
        using var concepts = database.Command("""
            SELECT a.concept_id,COUNT(*) AS n FROM semantic_content_cluster_members m
            JOIN image_classification_assertions a ON a.image_id=m.image_id
            WHERE m.cluster_key=$key AND a.source_kind='vision_model' AND a.concept_id NOT LIKE 'cluster.%'
              AND a.review_state IN ('pending','accepted')
            GROUP BY a.concept_id ORDER BY n DESC,a.concept_id LIMIT 2
            """);
        concepts.Parameters.AddWithValue("$key", key); using var conceptReader = concepts.ExecuteReader();
        var names = new List<string>(); while (conceptReader.Read()) names.Add(VisualConceptCatalog.DisplayName(conceptReader.GetString(0)));
        conceptReader.Close();
        using var summaries = database.Command("""
            SELECT v.summary FROM semantic_content_cluster_members m JOIN cloud_vision_analyses v ON v.image_id=m.image_id
            WHERE m.cluster_key=$key AND v.status='completed' AND length(trim(v.summary))>0
            ORDER BY v.updated_utc DESC LIMIT 1
            """);
        summaries.Parameters.AddWithValue("$key", key); var summary = summaries.ExecuteScalar() as string;
        var label = names.Count > 0 ? string.Join(" · ", names) : "相似照片";
        return string.IsNullOrWhiteSpace(summary) ? label : label + "｜" + summary[..Math.Min(24, summary.Length)];
    }

    private void Save(string modelVersion, IReadOnlyList<Draft> clusters)
    {
        using var transaction = database.BeginTransaction();
        foreach (var sql in new[] { "DELETE FROM image_classification_assertions WHERE source_kind='vision_model' AND source_version='semantic-cluster-v1'", "DELETE FROM semantic_content_clusters" })
        { using var clear = database.Command(sql); clear.Transaction = transaction; clear.ExecuteNonQuery(); }
        var now = DateTime.UtcNow.ToString("O");
        foreach (var cluster in clusters)
        {
            var ids = cluster.Members.Select(item => item.ImageId).Order().ToArray();
            var key = cluster.ExistingKey ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', ids.Take(8)))))[..16].ToLowerInvariant();
            var common = cluster.Members.SelectMany(item => item.Concepts).GroupBy(value => value)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key).Take(2)
                .Select(group => VisualConceptCatalog.DisplayName(group.Key)).ToArray();
            var summary = cluster.Members.Select(item => item.Summary).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var label = common.Length > 0 ? string.Join(" · ", common) : "相似照片";
            if (!string.IsNullOrWhiteSpace(summary)) label += "｜" + summary[..Math.Min(24, summary.Length)];
            using (var insert = database.Command("INSERT INTO semantic_content_clusters(cluster_key,model_version,display_name,confidence,member_count,created_utc,updated_utc) VALUES($key,$model,$label,$confidence,$count,$now,$now)"))
            { insert.Transaction = transaction; insert.Parameters.AddWithValue("$key", key); insert.Parameters.AddWithValue("$model", modelVersion); insert.Parameters.AddWithValue("$label", label); insert.Parameters.AddWithValue("$confidence", cluster.Members.Average(item => Cosine(item.Vector, cluster.Centroid))); insert.Parameters.AddWithValue("$count", ids.Length); insert.Parameters.AddWithValue("$now", now); insert.ExecuteNonQuery(); }
            foreach (var item in cluster.Members)
            {
                var score = Cosine(item.Vector, cluster.Centroid);
                using var member = database.Command("INSERT INTO semantic_content_cluster_members(cluster_key,image_id,similarity,semantic_signature) VALUES($key,$image,$score,$signature)");
                member.Transaction = transaction; member.Parameters.AddWithValue("$key", key); member.Parameters.AddWithValue("$image", item.ImageId); member.Parameters.AddWithValue("$score", score); member.Parameters.AddWithValue("$signature", item.Signature); member.ExecuteNonQuery();
                using var assertion = database.Command("INSERT INTO image_classification_assertions(image_id,concept_id,source_kind,source_version,assertion_state,review_state,confidence,evidence_ref,evidence_fingerprint,created_utc,updated_utc) VALUES($image,$concept,'vision_model','semantic-cluster-v1','automatic','accepted',$score,$key,$key,$now,$now)");
                assertion.Transaction = transaction; assertion.Parameters.AddWithValue("$image", item.ImageId); assertion.Parameters.AddWithValue("$concept", "cluster.semantic." + key); assertion.Parameters.AddWithValue("$score", score); assertion.Parameters.AddWithValue("$key", key); assertion.Parameters.AddWithValue("$now", now); assertion.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    private static float Cosine(float[] left, float[] right) { double dot = 0, a = 0, b = 0; for (var i = 0; i < left.Length; i++) { dot += left[i] * right[i]; a += left[i] * left[i]; b += right[i] * right[i]; } return a <= 0 || b <= 0 ? 0 : (float)(dot / Math.Sqrt(a * b)); }
    private static void Normalize(float[] value) { var norm = Math.Sqrt(value.Sum(item => item * item)); if (norm <= 0) return; for (var i = 0; i < value.Length; i++) value[i] = (float)(value[i] / norm); }
    public ContentClusterResult RebuildLatest(float? similarityThreshold = null, int minimumSize = 3)
    {
        var model = LatestModel();
        return string.IsNullOrWhiteSpace(model) ? new(0, 0, 0) : Rebuild(model, similarityThreshold, minimumSize);
    }

    private string? LatestModel()
    {
        using var command = database.Command("SELECT model_version FROM clip_image_embeddings GROUP BY model_version ORDER BY COUNT(*) DESC LIMIT 1");
        return command.ExecuteScalar() as string;
    }

    private float RecommendThreshold()
    {
        var model = LatestModel(); if (model is null) return 0.82f;
        var nearest = new List<float>();
        foreach (var bucket in Load(model).Where(item => item.Concepts.Length > 0).GroupBy(item => item.Signature))
        {
            var sample = bucket.Take(128).ToArray();
            for (var i = 0; i < sample.Length; i++)
            {
                var best = -1f;
                for (var j = 0; j < sample.Length; j++) if (i != j) best = Math.Max(best, Cosine(sample[i].Vector, sample[j].Vector));
                if (best >= 0) nearest.Add(best);
            }
        }
        if (nearest.Count < 8) return 0.82f;
        nearest.Sort();
        var percentile = nearest[(int)Math.Floor((nearest.Count - 1) * 0.60)];
        return Math.Clamp(MathF.Round((percentile - 0.02f) * 100) / 100, 0.76f, 0.90f);
    }
    public ContentClusterSettings GetSettings()
    {
        using var command = database.Command("SELECT automatic,manual_threshold,recommended_threshold,recommendation_fingerprint FROM semantic_cluster_settings WHERE id=1");
        using var reader = command.ExecuteReader();
        var automatic = true; var manual = 0.82f; var recommended = 0.82f; var storedFingerprint = "";
        if (reader.Read()) { automatic = reader.GetBoolean(0); manual = reader.GetFloat(1); recommended = reader.GetFloat(2); storedFingerprint = reader.GetString(3); }
        reader.Close();
        var fingerprint = EmbeddingFingerprint();
        if (!string.Equals(fingerprint, storedFingerprint, StringComparison.Ordinal))
        {
            recommended = RecommendThreshold();
            using var update = database.Command("UPDATE semantic_cluster_settings SET recommended_threshold=$recommended,recommendation_fingerprint=$fingerprint WHERE id=1");
            update.Parameters.AddWithValue("$recommended", recommended); update.Parameters.AddWithValue("$fingerprint", fingerprint); update.ExecuteNonQuery();
        }
        return new(automatic, manual, recommended, automatic ? recommended : manual);
    }

    public ContentClusterSettings SaveSettings(bool automatic, float manualThreshold)
    {
        var bounded = Math.Clamp(manualThreshold, 0.70f, 0.95f);
        using var command = database.Command("UPDATE semantic_cluster_settings SET automatic=$automatic,manual_threshold=$threshold,updated_utc=$now WHERE id=1");
        command.Parameters.AddWithValue("$automatic", automatic); command.Parameters.AddWithValue("$threshold", bounded);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
        return GetSettings();
    }

    public ContentClusterPreview Preview(float threshold)
    {
        var model = LatestModel(); if (model is null) return new(Math.Clamp(threshold, 0.70f, 0.95f), 0, 0);
        var bounded = Math.Clamp(threshold, 0.70f, 0.95f); var clusters = 0; var members = 0;
        foreach (var bucket in Load(model).Where(item => item.Concepts.Length > 0).GroupBy(item => item.Signature))
        {
            var drafts = new List<Draft>();
            foreach (var item in bucket.Take(1000))
            {
                var best = drafts.Select(cluster => (Cluster: cluster, Score: Cosine(item.Vector, cluster.Centroid)))
                    .OrderByDescending(value => value.Score).FirstOrDefault();
                if (best.Cluster is not null && best.Score >= bounded) best.Cluster.Add(item); else drafts.Add(new Draft(item));
            }
            var stable = drafts.Where(cluster => cluster.Members.Count >= 3).ToArray(); clusters += stable.Length; members += stable.Sum(cluster => cluster.Members.Count);
        }
        return new(bounded, clusters, members);
    }

    private string EmbeddingFingerprint()
    {
        using var command = database.Command("SELECT COUNT(*) || ':' || COALESCE(MAX(indexed_utc),'') FROM clip_image_embeddings");
        return command.ExecuteScalar()?.ToString() ?? "0:";
    }

}

