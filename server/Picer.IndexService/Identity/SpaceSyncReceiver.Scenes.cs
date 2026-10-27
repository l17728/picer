using System.Text.Json;
using Picer.Core.Gateway;
using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncReceiver
{
    private string BindSceneFingerprint(string publisher,string local)
    {
        var target=database.GetSceneTargetFingerprint(publisher,local)??DeviceAuthorization.Digest(publisher+":"+local);
        database.RememberSceneBinding(publisher,local,target,true);
        return target;
    }

    private bool SceneOwnedByPublisher(string publisher,string fingerprint,long? anchor=null) => Scalar("""
        SELECT 1 FROM scene_group_descriptions s WHERE s.fingerprint=$key AND json_array_length(s.image_ids_json)>0
        AND ($anchor IS NULL OR EXISTS(SELECT 1 FROM json_each(s.image_ids_json) WHERE value=$anchor))
        AND NOT EXISTS(SELECT 1 FROM json_each(s.image_ids_json) member WHERE NOT EXISTS(
            SELECT 1 FROM sync_active_entity_identities e WHERE e.publisher_id=$publisher AND e.kind='image' AND e.target_id=member.value))
        """,("$key",fingerprint),("$publisher",publisher),("$anchor",(object?)anchor??DBNull.Value)) is not null;

    private IReadOnlyList<SpaceSceneDescription> ReadPublisherScenes(string publisher,long image)
    {
        using var query=Sql("""
            SELECT fingerprint,directory_path,start_utc,end_utc,image_ids_json,representative_image_ids_json,provider,model_version,prompt_version,
                scene_name,summary,search_terms_json,state,created_utc,reviewed_utc FROM scene_group_descriptions s
            JOIN scene_group_members member ON member.scene_fingerprint=s.fingerprint WHERE member.image_id=$image
            """,("$image",image));
        var rows=new List<GatewaySceneGroupDescription>();
        using(var reader=query.ExecuteReader())while(reader.Read())rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),reader.GetString(8),reader.GetString(9),reader.GetString(10),reader.GetString(11),reader.GetString(12),reader.GetString(13),reader.GetString(14)));
        var result=new List<SpaceSceneDescription>();
        foreach(var scene in rows)
        {
            if(!SceneOwnedByPublisher(publisher,scene.Fingerprint) || database.GetSceneLocalFingerprint(publisher,scene.Fingerprint) is not {} local)continue;
            if(!PublishedMediaPath.TryUnqualify(scene.DirectoryPath,out var owner,out var path) || owner!=publisher)continue;
            string Translate(string json)=>JsonSerializer.Serialize((JsonSerializer.Deserialize<long[]>(json)??[]).Select(id=>
                long.Parse((string)Scalar("SELECT local_key FROM sync_active_entity_identities WHERE publisher_id=$publisher AND kind='image' AND target_id=$id",("$publisher",publisher),("$id",id))!,System.Globalization.CultureInfo.InvariantCulture)));
            result.Add(new(scene with {Fingerprint=local,DirectoryPath=path,ImageIdsJson=Translate(scene.ImageIdsJson),RepresentativeImageIdsJson=Translate(scene.RepresentativeImageIdsJson)},scene.Fingerprint));
        }
        return result;
    }
}
