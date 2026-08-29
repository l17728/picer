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
