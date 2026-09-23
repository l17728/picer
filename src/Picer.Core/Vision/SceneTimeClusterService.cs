using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed record SceneTimeCluster(DateTime StartUtc, DateTime EndUtc, string SceneName, IReadOnlyList<long> ImageIds)
{
    public string DisplayName => $"{StartUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {SceneName}";
}

public sealed class SceneTimeClusterService(PicerDatabase database)
{
    public IReadOnlyList<SceneTimeCluster> Build(string directoryPath, bool includeDescendants = false, TimeSpan? maximumGap = null) =>
        Build(database.GetActiveImagesInDirectory(directoryPath, includeDescendants), maximumGap);

    public IReadOnlyList<SceneTimeCluster> Build(IReadOnlyList<ImageRecord> images, TimeSpan? maximumGap = null)
    {
        if (images.Count == 0) return [];
        var gap = maximumGap ?? TimeSpan.FromHours(3);
        var sceneByImage = LoadScenes(images.Select(image => image.Id));
        var ordered = images.OrderBy(EffectiveTime).ThenBy(image => image.Id).ToArray();
        var timeGroups = new List<List<ImageRecord>>();
        var current = new List<ImageRecord>();
        foreach (var image in ordered)
        {
            if (current.Count > 0 && EffectiveTime(image) - EffectiveTime(current[^1]) > gap)
            {
                timeGroups.Add(current);
                current = [];
            }
            current.Add(image);
        }
        if (current.Count > 0) timeGroups.Add(current);

        var result = new List<SceneTimeCluster>();
        var descriptions = new SceneGroupDescriptionService(database, new CloudVisionRepository(database));
        foreach (var group in timeGroups)
        {
            var accepted = descriptions.GetAccepted(SceneGroupDescriptionService.CreateFingerprint(group))
                ?? descriptions.GetAcceptedForGroup(Path.GetDirectoryName(group[0].Path) ?? string.Empty, group);
            if (accepted is not null)
            {
                result.Add(Create(group, accepted.SceneName));
                continue;
            }

            // Representative-image analysis must not classify the entire group. Only split by model scenes
            // after every image in this time group has a usable scene result.
            if (group.Any(image => !sceneByImage.ContainsKey(image.Id)))
            {
                result.Add(Create(group, "未分类场景"));
                continue;
            }

            var sceneGroup = new List<ImageRecord>();
            string? sceneName = null;
            foreach (var image in group)
            {
                var imageScene = sceneByImage[image.Id];
                if (sceneGroup.Count > 0 && !string.Equals(sceneName, imageScene, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(CreateWithAccepted(sceneGroup, sceneName!, descriptions));
                    sceneGroup = [];
                }
                sceneName = imageScene;
                sceneGroup.Add(image);
            }
            if (sceneGroup.Count > 0) result.Add(CreateWithAccepted(sceneGroup, sceneName!, descriptions));
        }
        return result;
    }

    private static SceneTimeCluster Create(IReadOnlyList<ImageRecord> images, string sceneName) =>
        new(EffectiveTime(images[0]), EffectiveTime(images[^1]), sceneName, images.Select(image => image.Id).ToArray());

    private static SceneTimeCluster CreateWithAccepted(IReadOnlyList<ImageRecord> images, string fallbackSceneName,
        SceneGroupDescriptionService descriptions)
    {
        var accepted = descriptions.GetAccepted(SceneGroupDescriptionService.CreateFingerprint(images))
            ?? descriptions.GetAcceptedForGroup(Path.GetDirectoryName(images[0].Path) ?? string.Empty, images);
        return Create(images, accepted?.SceneName ?? fallbackSceneName);
    }

    private Dictionary<long, string> LoadScenes(IEnumerable<long> imageIds)
    {
        var ids = imageIds.ToArray();
        var result = new Dictionary<long, string>();
        foreach (var batch in ids.Chunk(400))
        {
            var names = batch.Select((_, index) => $"$id{index}").ToArray();
            using var command = database.Command($"SELECT image_id,result_json FROM cloud_vision_analyses WHERE status='completed' AND image_id IN ({string.Join(',', names)}) ORDER BY updated_utc DESC");
            for (var index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(names[index], batch[index]);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (result.ContainsKey(id)) continue;
                try
                {
                    var value = JsonSerializer.Deserialize<CloudVisionResult>(reader.GetString(1), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    var scene = value?.Scenes?.FirstOrDefault() ?? value?.Activities?.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(scene)) result[id] = Localize(scene);
                }
                catch (JsonException) { }
            }
        }
        return result;
    }

    public static DateTime EffectiveTime(ImageRecord image) =>
        (image.TakenUtc ?? image.CreatedUtc ?? image.ModifiedUtc).ToUniversalTime();

    private static string Localize(string scene)
    {
        var key = scene.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        return key switch
        {
            "mountain" or "mountains" => "山地风景", "beach" or "seaside" => "海边沙滩",
            "restaurant" or "dining" => "聚餐用餐", "indoor" => "室内活动", "outdoor" => "户外活动",
            "city" or "urban" => "城市街景", "park" => "公园游览", "snow" or "snowy" => "冰雪场景",
            "hiking" => "徒步登山", "family" or "family gathering" => "家庭聚会", "portrait" => "人物合影",
            _ => scene
        };
    }
}
