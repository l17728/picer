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
    public IReadOnlyList<SceneTimeCluster> Build(string directoryPath, bool includeDescendants = false, TimeSpan? maximumGap = null)
    {
        var images=database.GetActiveImagesInDirectory(directoryPath,includeDescendants);
        return Build(images, maximumGap);
    }

    public IReadOnlyList<SceneTimeCluster> Build(IReadOnlyList<ImageRecord> images, TimeSpan? maximumGap = null)
    {
        var gap=maximumGap??TimeSpan.FromHours(3);
        if(images.Count==0)return[];
        var sceneByImage=LoadScenes(images.Select(image=>image.Id));var clusters=new List<SceneTimeCluster>();
        var current=new List<ImageRecord>();string currentScene="未分类场景";
        foreach(var image in images.OrderBy(EffectiveTime).ThenBy(image=>image.Id))
        {
            var scene=sceneByImage.GetValueOrDefault(image.Id,"未分类场景");
            var split=current.Count>0&&(EffectiveTime(image)-EffectiveTime(current[^1])>gap ||
                (scene!="未分类场景"&&currentScene!="未分类场景"&&!string.Equals(scene,currentScene,StringComparison.OrdinalIgnoreCase)));
            if(split){AddCluster();current.Clear();}
            if(current.Count==0)currentScene=scene;else if(currentScene=="未分类场景"&&scene!="未分类场景")currentScene=scene;
            current.Add(image);
        }
        AddCluster();return clusters;
        void AddCluster(){if(current.Count==0)return;var accepted=new SceneGroupDescriptionService(database,new CloudVisionRepository(database)).GetAccepted(SceneGroupDescriptionService.CreateFingerprint(current));clusters.Add(new(EffectiveTime(current[0]),EffectiveTime(current[^1]),accepted?.SceneName??currentScene,current.Select(image=>image.Id).ToArray()));}
    }

    private Dictionary<long,string> LoadScenes(IEnumerable<long> imageIds)
    {
        var ids=imageIds.ToArray();var result=new Dictionary<long,string>();
        foreach(var batch in ids.Chunk(400)){var names=batch.Select((_,i)=>$"$id{i}").ToArray();using var command=database.Command($"SELECT image_id,result_json FROM cloud_vision_analyses WHERE status='completed' AND image_id IN ({string.Join(',',names)}) ORDER BY updated_utc DESC");for(var i=0;i<batch.Length;i++)command.Parameters.AddWithValue(names[i],batch[i]);using var reader=command.ExecuteReader();
            while(reader.Read()){var id=reader.GetInt64(0);if(result.ContainsKey(id))continue;try{var value=JsonSerializer.Deserialize<CloudVisionResult>(reader.GetString(1),new JsonSerializerOptions{PropertyNameCaseInsensitive=true});var scene=value?.Scenes.FirstOrDefault()??value?.Activities.FirstOrDefault();if(!string.IsNullOrWhiteSpace(scene))result[id]=scene;}catch(JsonException){}}}
        return result.ToDictionary(pair=>pair.Key,pair=>Localize(pair.Value));
    }
    public static DateTime EffectiveTime(ImageRecord image)=>(image.TakenUtc??image.CreatedUtc??image.ModifiedUtc).ToUniversalTime();

    private static string Localize(string scene)
    {
        var key=scene.Trim().ToLowerInvariant().Replace('_',' ').Replace('-',' ');
        return key switch
        {
            "mountain" or "mountains"=>"山地风景", "beach" or "seaside"=>"海边沙滩",
            "restaurant" or "dining"=>"聚餐用餐", "indoor"=>"室内活动", "outdoor"=>"户外活动",
            "city" or "urban"=>"城市街景", "park"=>"公园游览", "snow" or "snowy"=>"冰雪场景",
            "hiking"=>"徒步登山", "family" or "family gathering"=>"家庭聚会", "portrait"=>"人物合影",
            _=>scene
        };
    }
}
