using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed record SceneDescriptionText(string SceneName,string Summary,IReadOnlyList<string> SearchTerms,
    string Provider,string ModelVersion,string PromptVersion);

public sealed partial class PicerDatabase
{
    private MetadataEdit NormalizeSceneMetadataEdit(MetadataEdit edit,SqliteTransaction? transaction)
    {
        if(edit.Kind!="scene-description")return edit;
        using var query=Command("SELECT MIN(CAST(member.value AS INTEGER)) FROM scene_group_descriptions s JOIN json_each(s.image_ids_json) member JOIN images i ON i.id=member.value WHERE s.fingerprint=$key");
        query.Transaction=transaction;query.Parameters.AddWithValue("$key",edit.Field);
        return query.ExecuteScalar() is long anchor?edit with {EntityId=anchor}:edit;
    }

    public void ReplaySceneDescriptionEdit(string fingerprint,SqliteTransaction transaction)
    {
        using var query=Command("SELECT kind,entity_id,field,value,updated_utc,operation_id FROM metadata_edits WHERE kind='scene-description' AND field=$key LIMIT 1");
        query.Transaction=transaction;query.Parameters.AddWithValue("$key",fingerprint);
        MetadataEdit? edit=null;
        using(var row=query.ExecuteReader())if(row.Read())edit=new(row.GetString(0),row.GetInt64(1),row.GetString(2),row.GetString(3),row.GetString(4),row.GetString(5));
        if(edit is not null)ApplySceneDescriptionEdit(edit,transaction);
    }
    public static bool TryReadSceneDescriptionText(string value,out SceneDescriptionText? text)
    {
        text=null;
        if(value.Length>65_536)return false;
        try { text=JsonSerializer.Deserialize<SceneDescriptionText>(value); }
        catch(JsonException) { return false; }
        return text is not null && !string.IsNullOrWhiteSpace(text.SceneName) && text.SceneName.Length<=200 && text.Summary is {Length:<=8000}
            && text.SearchTerms is {Count:<=20} && text.SearchTerms.All(term=>!string.IsNullOrWhiteSpace(term) && term.Length<=80)
            && text.Provider is {Length:>0 and <=256} && text.ModelVersion is {Length:<=256} && text.PromptVersion is {Length:<=256};
    }

    public string? GetSceneTargetFingerprint(string publisher,string localFingerprint)
    {
        using var query=Command("SELECT target_fingerprint FROM sync_scene_bindings WHERE publisher_id=$publisher AND local_fingerprint=$local");
        query.Parameters.AddWithValue("$publisher",publisher);query.Parameters.AddWithValue("$local",localFingerprint);return query.ExecuteScalar() as string;
    }

    public string? GetSceneLocalFingerprint(string publisher,string targetFingerprint)
    {
        using var query=Command("SELECT local_fingerprint FROM sync_scene_bindings WHERE publisher_id=$publisher AND target_fingerprint=$target ORDER BY source_confirmed DESC,local_fingerprint LIMIT 1");
        query.Parameters.AddWithValue("$publisher",publisher);query.Parameters.AddWithValue("$target",targetFingerprint);return query.ExecuteScalar() as string;
    }

    public void RememberSceneBinding(string publisher,string localFingerprint,string targetFingerprint,bool sourceConfirmed)
    {
        if(GetSceneTargetFingerprint(publisher,localFingerprint) is {} existing && existing!=targetFingerprint)
            throw new InvalidDataException("scene_identity_changed");
        using var insert=Command("""
            INSERT INTO sync_scene_bindings(publisher_id,local_fingerprint,target_fingerprint,source_confirmed) VALUES($publisher,$local,$target,$confirmed)
            ON CONFLICT(publisher_id,local_fingerprint) DO UPDATE SET source_confirmed=MAX(sync_scene_bindings.source_confirmed,excluded.source_confirmed)
            """);insert.Parameters.AddWithValue("$publisher",publisher);insert.Parameters.AddWithValue("$local",localFingerprint);
        insert.Parameters.AddWithValue("$target",targetFingerprint);insert.Parameters.AddWithValue("$confirmed",sourceConfirmed?1:0);insert.ExecuteNonQuery();
    }

    public void RecordSceneDescriptionEdit(string fingerprint,SqliteTransaction transaction)
    {
        using var query=Command("""
            SELECT scene_name,summary,search_terms_json,provider,model_version,prompt_version,
                (SELECT MIN(CAST(value AS INTEGER)) FROM json_each(image_ids_json))
            FROM scene_group_descriptions WHERE fingerprint=$key AND state='accepted'
            """);query.Transaction=transaction;query.Parameters.AddWithValue("$key",fingerprint);
        SceneDescriptionText text;long anchor;
        using(var row=query.ExecuteReader())
        {
            if(!row.Read() || row.IsDBNull(6))throw new InvalidOperationException("scene_members_missing");
            text=new(row.GetString(0),row.GetString(1),JsonSerializer.Deserialize<string[]>(row.GetString(2))??[],row.GetString(3),row.GetString(4),row.GetString(5));anchor=row.GetInt64(6);
        }
        RecordMetadataEdit("scene-description",anchor,fingerprint,JsonSerializer.Serialize(text),transaction);
    }

    private bool SceneMetadataEntityExists(MetadataEdit edit,SqliteTransaction transaction)
    {
        using var query=Command("SELECT 1 FROM scene_group_descriptions s JOIN json_each(s.image_ids_json) member JOIN images i ON i.id=member.value WHERE s.fingerprint=$key AND i.id=$image");
        query.Transaction=transaction;query.Parameters.AddWithValue("$key",edit.Field);query.Parameters.AddWithValue("$image",edit.EntityId);return query.ExecuteScalar() is not null;
    }

    private void ApplySceneDescriptionEdit(MetadataEdit edit,SqliteTransaction transaction)
    {
        if(!TryReadSceneDescriptionText(edit.Value,out var text))throw new ArgumentException("invalid_scene_description");
        using var update=Command("""
            UPDATE scene_group_descriptions SET scene_name=$name,summary=$summary,search_terms_json=$terms,
                provider=$provider,model_version=$model,prompt_version=$prompt,state='accepted',reviewed_utc=$updated
            WHERE fingerprint=$key AND (scene_name IS NOT $name OR summary IS NOT $summary OR search_terms_json IS NOT $terms
                OR provider IS NOT $provider OR model_version IS NOT $model OR prompt_version IS NOT $prompt OR state<>'accepted')
            """);update.Transaction=transaction;update.Parameters.AddWithValue("$key",edit.Field);update.Parameters.AddWithValue("$name",text!.SceneName);
        update.Parameters.AddWithValue("$summary",text.Summary);update.Parameters.AddWithValue("$terms",JsonSerializer.Serialize(text.SearchTerms));
        update.Parameters.AddWithValue("$provider",text.Provider);update.Parameters.AddWithValue("$model",text.ModelVersion);
        update.Parameters.AddWithValue("$prompt",text.PromptVersion);update.Parameters.AddWithValue("$updated",edit.UpdatedUtc);update.ExecuteNonQuery();
    }
}
