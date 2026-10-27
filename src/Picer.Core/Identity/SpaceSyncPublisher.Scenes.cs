using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Gateway;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    private static bool IsSceneInScope(PicerDatabase database,SqliteTransaction transaction,string fingerprint,SpaceUploadScope scope)
    {
        using var query=database.Command("SELECT image_ids_json FROM scene_group_descriptions WHERE fingerprint=$key");
        query.Transaction=transaction;query.Parameters.AddWithValue("$key",fingerprint);
        return query.ExecuteScalar() is string json && JsonSerializer.Deserialize<long[]>(json) is {Length:>0} ids
            && ids.All(id=>IsMediaInScope(database,transaction,"image",id,scope));
    }

    private static void ApplySceneChanges(PicerDatabase database,SqliteTransaction transaction,string publisher,long image,SpaceMetadataChange change,SpaceUploadScope? scope)
    {
        foreach(var item in change.SceneDescriptions??[])
        {
            var scene=item.Scene;
            var ids=JsonSerializer.Deserialize<long[]>(scene.ImageIdsJson)??[];
            if(!ids.Contains(image) || ids.Length>10000)throw new InvalidDataException("scene_members_invalid");
            foreach(var id in ids)
            {
                using var owner=database.Command("SELECT 1 FROM sync_active_entity_identities WHERE publisher_id=$publisher AND kind='image' AND local_key=$id");
                owner.Transaction=transaction;owner.Parameters.AddWithValue("$publisher",publisher);owner.Parameters.AddWithValue("$id",id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if(owner.ExecuteScalar() is null)throw new InvalidDataException("scene_source_mismatch");
            }
            if(ids.Any(id=>!IsMediaInScope(database,transaction,"image",id,scope)))continue;
            var representatives=JsonSerializer.Deserialize<long[]>(scene.RepresentativeImageIdsJson)??[];
            if(representatives.Any(id=>!ids.Contains(id)))throw new InvalidDataException("scene_representatives_invalid");
            database.RememberSceneBinding(publisher,scene.Fingerprint,item.RemoteFingerprint,false);
            database.UpsertMirroredVideoMetadata(new GatewayMetadata([],[],[],[],[],[],[],[],[],SceneGroupDescriptions:[scene]),externalTransaction:transaction);
            database.ReplaySceneDescriptionEdit(scene.Fingerprint,transaction);
        }
    }
}
