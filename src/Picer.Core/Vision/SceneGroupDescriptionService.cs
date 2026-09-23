using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed record SceneGroupPlan(
    string Fingerprint,
    string DirectoryPath,
    DateTime StartUtc,
    DateTime EndUtc,
    IReadOnlyList<ImageRecord> Images,
    IReadOnlyList<IReadOnlyList<ImageRecord>> Subgroups,
    IReadOnlyList<ImageRecord> Representatives);

public sealed record SceneGroupDescription(
    string Fingerprint,
    string SceneName,
    string Summary,
    IReadOnlyList<string> SearchTerms,
    string Provider,
    string ModelVersion,
    string PromptVersion,
    string State);

public sealed class SceneGroupDescriptionService(PicerDatabase database, CloudVisionRepository cloud)
{
    private const string LayoutProvider = "picer-local-layout";

    /// <summary>Persists the local directory scene layout so a remote mirror can render
    /// exactly the same ordered groups without independently re-clustering.</summary>
    public void SaveLayoutSnapshot(string directoryPath, bool includeDescendants, IReadOnlyList<SceneTimeCluster> clusters)
    {
        var directory = Path.GetFullPath(directoryPath);
        var layoutVersion = includeDescendants ? "layout-v1:recursive" : "layout-v1:directory";
        var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cluster in clusters)
        {
            var imageIds = cluster.ImageIds.ToArray();
            if (imageIds.Length == 0) continue;
            var fingerprint = CreateLayoutFingerprint(directory, includeDescendants, imageIds);
            fingerprints.Add(fingerprint);
            using var command = database.Command("""
                INSERT INTO scene_group_descriptions(fingerprint,directory_path,start_utc,end_utc,image_ids_json,
                  representative_image_ids_json,provider,model_version,prompt_version,scene_name,summary,search_terms_json,
                  state,created_utc,reviewed_utc)
                VALUES($fingerprint,$directory,$start,$end,$images,'[]',$provider,'layout-v1',$layout,$scene,'','[]','layout',$now,$now)
                ON CONFLICT(fingerprint) DO UPDATE SET directory_path=excluded.directory_path,start_utc=excluded.start_utc,
                  end_utc=excluded.end_utc,image_ids_json=excluded.image_ids_json,scene_name=excluded.scene_name,
                  prompt_version=excluded.prompt_version,state='layout',reviewed_utc=excluded.reviewed_utc
                """);
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$directory", directory);
            command.Parameters.AddWithValue("$start", cluster.StartUtc.ToString("O"));
            command.Parameters.AddWithValue("$end", cluster.EndUtc.ToString("O"));
            command.Parameters.AddWithValue("$images", JsonSerializer.Serialize(imageIds));
            command.Parameters.AddWithValue("$provider", LayoutProvider);
            command.Parameters.AddWithValue("$layout", layoutVersion);
            command.Parameters.AddWithValue("$scene", cluster.SceneName);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        using var cleanup = database.Command("DELETE FROM scene_group_descriptions WHERE state='layout' AND directory_path=$directory AND prompt_version=$layout AND fingerprint NOT IN (SELECT value FROM json_each($fingerprints))");
        cleanup.Parameters.AddWithValue("$directory", directory);
        cleanup.Parameters.AddWithValue("$layout", layoutVersion);
        cleanup.Parameters.AddWithValue("$fingerprints", JsonSerializer.Serialize(fingerprints));
        cleanup.ExecuteNonQuery();
    }

    public IReadOnlyList<SceneTimeCluster> GetLayoutSnapshot(string directoryPath, bool includeDescendants, IReadOnlyList<ImageRecord> images)
    {
        var directory = Path.GetFullPath(directoryPath);
        var layoutVersion = includeDescendants ? "layout-v1:recursive" : "layout-v1:directory";
        var byId = images.ToDictionary(image => image.Id);
        using var command = database.Command("SELECT start_utc,end_utc,image_ids_json,scene_name FROM scene_group_descriptions WHERE state='layout' AND directory_path=$directory AND prompt_version=$layout ORDER BY start_utc, fingerprint");
        command.Parameters.AddWithValue("$directory", directory); command.Parameters.AddWithValue("$layout", layoutVersion);
        using var reader = command.ExecuteReader();
        var result = new List<SceneTimeCluster>();
        while (reader.Read())
        {
            long[] ids;
            try { ids = JsonSerializer.Deserialize<long[]>(reader.GetString(2)) ?? []; } catch (JsonException) { continue; }
            var resolved = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
            if (resolved.Length == 0 || resolved.Length != ids.Length) continue;
            if (!DateTime.TryParse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind, out var start) ||
                !DateTime.TryParse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind, out var end)) continue;
            result.Add(new SceneTimeCluster(start, end, reader.GetString(3), ids));
        }
        return result;
    }

    public static string CreateLayoutFingerprint(string directoryPath, bool includeDescendants, IEnumerable<long> imageIds)
    {
        var text = $"layout-v1|{Path.GetFullPath(directoryPath)}|{includeDescendants}|{string.Join(',', imageIds)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
    public async Task<SceneGroupDescription?> SummarizeWithAiAsync(SceneGroupPlan plan,
        ISceneGroupSummarizer summarizer, CancellationToken cancellationToken = default)
    {
        var descriptions = plan.Representatives.Select((image, index) =>
        {
            var completed = cloud.GetLatestCompleted(image.Id);
            return completed is null ? null : new SceneRepresentativeDescription(index + 1,
                SceneTimeClusterService.EffectiveTime(image), completed.Value.Result.Summary,
                completed.Value.Result.Scenes ?? [], completed.Value.Result.Activities ?? [],
                completed.Value.Result.Objects ?? []);
        }).Where(item => item is not null).Cast<SceneRepresentativeDescription>().ToArray();
        if (descriptions.Length == 0) return null;
        var result = await summarizer.SummarizeAsync(descriptions, cancellationToken).ConfigureAwait(false);
        return new(plan.Fingerprint, result.SceneName.Trim(), result.Summary.Trim(),
            result.SearchTerms.Where(term => !string.IsNullOrWhiteSpace(term)).Select(term => term.Trim())
                .Distinct(StringComparer.CurrentCultureIgnoreCase).Take(20).ToArray(),
            summarizer.Provider, summarizer.ModelVersion, summarizer.PromptVersion, "accepted");
    }

    public SceneGroupPlan BuildPlan(string directoryPath, IReadOnlyList<ImageRecord> images,
        int maximumSubgroups = 5, int maximumRepresentatives = 3)
    {
        if (images.Count == 0) throw new ArgumentException("场景组没有照片。", nameof(images));
        var ordered = images.OrderBy(SceneTimeClusterService.EffectiveTime).ThenBy(x => x.Id).ToArray();
        var subgroupCount = Math.Clamp(Math.Min(maximumSubgroups, ordered.Length), 1, ordered.Length);
        var subgroups = SplitAtLargestTimeGaps(ordered, subgroupCount);
        var representativeCount = Math.Clamp(Math.Min(maximumRepresentatives, subgroups.Count), 1, subgroups.Count);
        var selectedIndices = EvenlySpacedIndices(subgroups.Count, representativeCount);
        var representatives = selectedIndices.Select(index => subgroups[index][0]).ToArray();
        return new(CreateFingerprint(ordered), Path.GetFullPath(directoryPath),
            SceneTimeClusterService.EffectiveTime(ordered[0]), SceneTimeClusterService.EffectiveTime(ordered[^1]),
            ordered, subgroups, representatives);
    }

    public SceneGroupDescription? Summarize(SceneGroupPlan plan)
    {
        var descriptions = plan.Representatives.Select(image => cloud.GetLatestCompleted(image.Id)).Where(x => x is not null)
            .Select(x => x!.Value).ToArray();
        if (descriptions.Length == 0) return null;
        var scenes = descriptions.SelectMany(x => x.Result.Scenes).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        var activities = descriptions.SelectMany(x => x.Result.Activities).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        var rankedScenes = scenes.Concat(activities).GroupBy(x => x.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase).Take(2).Select(x => x.Key).ToArray();
        var sceneName = rankedScenes.Length > 0 ? string.Join(" · ", rankedScenes) : "已描述场景";
        var summaries = descriptions.Select(x => x.Result.Summary.Trim()).Where(x => x.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Take(3).ToArray();
        var summary = summaries.Length > 0 ? string.Join("；", summaries) : sceneName;
        var terms = descriptions.SelectMany(x => x.Result.SearchTerms.Concat(x.Result.Objects).Concat(x.Result.Activities).Concat(x.Result.Scenes))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(20).ToArray();
        var identity = descriptions[0];
        return new(plan.Fingerprint, sceneName, summary, terms, identity.Provider, identity.ModelVersion, identity.PromptVersion, "accepted");
    }

    public void SaveAccepted(SceneGroupPlan plan, SceneGroupDescription description)
    {
        using var command = database.Command("""
            INSERT INTO scene_group_descriptions(fingerprint,directory_path,start_utc,end_utc,image_ids_json,
              representative_image_ids_json,provider,model_version,prompt_version,scene_name,summary,search_terms_json,
              state,created_utc,reviewed_utc)
            VALUES($fingerprint,$directory,$start,$end,$images,$representatives,$provider,$model,$prompt,$scene,$summary,$terms,
              'accepted',$now,$now)
            ON CONFLICT(fingerprint) DO UPDATE SET provider=excluded.provider,model_version=excluded.model_version,
              prompt_version=excluded.prompt_version,scene_name=excluded.scene_name,summary=excluded.summary,
              search_terms_json=excluded.search_terms_json,state='accepted',reviewed_utc=excluded.reviewed_utc
            """);
        command.Parameters.AddWithValue("$fingerprint", plan.Fingerprint); command.Parameters.AddWithValue("$directory", plan.DirectoryPath);
        command.Parameters.AddWithValue("$start", plan.StartUtc.ToString("O")); command.Parameters.AddWithValue("$end", plan.EndUtc.ToString("O"));
        command.Parameters.AddWithValue("$images", JsonSerializer.Serialize(plan.Images.Select(x => x.Id)));
        command.Parameters.AddWithValue("$representatives", JsonSerializer.Serialize(plan.Representatives.Select(x => x.Id)));
        command.Parameters.AddWithValue("$provider", description.Provider); command.Parameters.AddWithValue("$model", description.ModelVersion);
        command.Parameters.AddWithValue("$prompt", description.PromptVersion); command.Parameters.AddWithValue("$scene", description.SceneName);
        command.Parameters.AddWithValue("$summary", description.Summary); command.Parameters.AddWithValue("$terms", JsonSerializer.Serialize(description.SearchTerms));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }

    public SceneGroupDescription? GetAccepted(string fingerprint)
    {
        using var command = database.Command("SELECT scene_name,summary,search_terms_json,provider,model_version,prompt_version,state FROM scene_group_descriptions WHERE fingerprint=$fingerprint AND state='accepted'");
        command.Parameters.AddWithValue("$fingerprint", fingerprint); using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var terms = JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [];
        return new(fingerprint, reader.GetString(0), reader.GetString(1), terms, reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6));
    }

    /// <summary>
    /// Resolves a description when image ids came from a different mirror
    /// database. The scene fingerprint historically used SQLite ids, which are
    /// not guaranteed to be stable across a local/server bootstrap. Directory
    /// and effective-time bounds are stable metadata and provide a safe
    /// compatibility fallback.
    /// </summary>
    public SceneGroupDescription? GetAcceptedForGroup(string directoryPath, IReadOnlyList<ImageRecord> images)
    {
        if (images.Count == 0) return null;
        var start = SceneTimeClusterService.EffectiveTime(images.MinBy(SceneTimeClusterService.EffectiveTime)!);
        var end = SceneTimeClusterService.EffectiveTime(images.MaxBy(SceneTimeClusterService.EffectiveTime)!);
        using var command = database.Command("""
            SELECT fingerprint,start_utc,end_utc,image_ids_json,scene_name,summary,search_terms_json,provider,model_version,prompt_version,state
            FROM scene_group_descriptions
            WHERE state='accepted' AND (directory_path=$directory OR lower(directory_path)=lower($directory))
              AND start_utc <= $end AND end_utc >= $start
            """);
        command.Parameters.AddWithValue("$directory", Path.GetFullPath(directoryPath));
        command.Parameters.AddWithValue("$start", start.ToString("O"));
        command.Parameters.AddWithValue("$end", end.ToString("O"));
        using var reader = command.ExecuteReader();
        SceneGroupDescription? best = null;
        var bestScore = double.MinValue;
        while (reader.Read())
        {
            if (!DateTime.TryParse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind, out var candidateStart) ||
                !DateTime.TryParse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind, out var candidateEnd))
                continue;
            var overlapStart = candidateStart > start ? candidateStart : start;
            var overlapEnd = candidateEnd < end ? candidateEnd : end;
            if (overlapEnd < overlapStart) continue;
            var unionSeconds = Math.Max(1, (Math.Max(candidateEnd.Ticks, end.Ticks) - Math.Min(candidateStart.Ticks, start.Ticks)) / (double)TimeSpan.TicksPerSecond);
            var overlapSeconds = Math.Max(0, (overlapEnd - overlapStart).TotalSeconds);
            var temporalScore = overlapSeconds / unionSeconds;
            var candidateCount = 0;
            try { candidateCount = JsonSerializer.Deserialize<long[]>(reader.GetString(3))?.Length ?? 0; } catch (JsonException) { }
            var countScore = 1d - Math.Min(1d, Math.Abs(candidateCount - images.Count) / (double)Math.Max(candidateCount, images.Count));
            var score = temporalScore * .7 + countScore * .3;
            if (score <= bestScore) continue;
            var terms = JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [];
            bestScore = score;
            best = new(reader.GetString(0), reader.GetString(4), reader.GetString(5), terms,
                reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10));
        }
        return best;
    }

    public void UpdateAcceptedText(string fingerprint, string sceneName, string summary)
    {
        if (string.IsNullOrWhiteSpace(sceneName)) throw new ArgumentException("场景标题不能为空。", nameof(sceneName));
        using var command = database.Command("""
            UPDATE scene_group_descriptions
            SET scene_name=$scene, summary=$summary, provider='user', model_version='manual-edit',
                prompt_version='manual-edit-v1', reviewed_utc=$now
            WHERE fingerprint=$fingerprint AND state='accepted'
            """);
        command.Parameters.AddWithValue("$scene", sceneName.Trim());
        command.Parameters.AddWithValue("$summary", summary.Trim());
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        if (command.ExecuteNonQuery() == 0) throw new InvalidOperationException("场景描述不存在或已经失效。");
    }

    public static string CreateFingerprint(IEnumerable<ImageRecord> images)
    {
        var text = string.Join('|', images.OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.FileSize}:{x.ModifiedUtc.ToUniversalTime():O}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static IReadOnlyList<int> EvenlySpacedIndices(int count, int selected)
    {
        if (count < 1 || selected < 1 || selected > count) throw new ArgumentOutOfRangeException();
        if (selected == 1) return [0];
        return Enumerable.Range(0, selected).Select(i => (int)Math.Round(i * (count - 1d) / (selected - 1d)))
            .Distinct().ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<ImageRecord>> SplitAtLargestTimeGaps(ImageRecord[] ordered, int count)
    {
        if (count == 1) return [ordered];
        var boundaries = Enumerable.Range(1, ordered.Length - 1)
            .Select(index => (Index: index, Gap: SceneTimeClusterService.EffectiveTime(ordered[index]) - SceneTimeClusterService.EffectiveTime(ordered[index - 1])))
            .OrderByDescending(x => x.Gap).ThenBy(x => x.Index).Take(count - 1).Select(x => x.Index).Order().ToArray();
        var result = new List<IReadOnlyList<ImageRecord>>(); var start = 0;
        foreach (var boundary in boundaries.Append(ordered.Length)) { result.Add(ordered[start..boundary]); start = boundary; }
        return result;
    }
}
