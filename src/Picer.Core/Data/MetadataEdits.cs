using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed record MetadataEdit(string Kind, long EntityId, string Field, string Value, string UpdatedUtc, string OperationId)
{
    public string? VideoContentIdentity { get; init; }
    public string? PhotoContentIdentity { get; init; }
    public long ServerVersion { get; init; }
    public long BaseVersion { get; init; }
}

public sealed partial class PicerDatabase
{
    // Local pending operations retain their known server baseline. Timestamps
    // order unversioned offline edits only; the service uses causal acceptance.
    public void RecordMetadataEdit(string kind, long id, string field, string value, SqliteTransaction transaction)
        =>RecordMetadataEditCore(kind,id,field,value,transaction);

    private MetadataEdit RecordMetadataEditCore(string kind, long id, string field, string value, SqliteTransaction transaction)
    {
        using var latest = Command("SELECT updated_utc FROM metadata_edits WHERE kind=$kind AND entity_id=$id AND field=$field");
        if (transaction is not null) latest.Transaction = transaction;
        latest.Parameters.AddWithValue("$kind", kind); latest.Parameters.AddWithValue("$id", id); latest.Parameters.AddWithValue("$field", field);
        var now = DateTime.UtcNow;
        if (latest.ExecuteScalar() is string previous && DateTime.Parse(previous, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) >= now)
            now = DateTime.Parse(previous, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddTicks(1);
        var operation = new MetadataEdit(kind, id, field, value, now.ToString("O"), Guid.NewGuid().ToString("N"));
        if (IsVideoMetadataEdit(operation) || IsPhotoClassificationEdit(operation))
        {
            operation = BindVideoMetadataIdentity(operation, transaction);
            var winner = FindMetadataEdit(operation, transaction);
            if (winner is not null && DateTime.Parse(winner.UpdatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) >= now)
                now = DateTime.Parse(winner.UpdatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddTicks(1);
        }
        var current = FindMetadataEdit(operation, transaction);
        operation=operation with { UpdatedUtc = now.ToString("O"), BaseVersion = current is null ? 0 : Math.Max(current.ServerVersion, current.BaseVersion) };
        SaveMetadataEdit(operation, transaction, force: true);
        return operation;
    }

    private void SaveMetadataEdit(MetadataEdit edit, SqliteTransaction? transaction, bool canonicalize = true, bool force = false)
    {
        ValidateMetadataEdit(edit);
        edit=NormalizeFaceManualEdit(edit);
        edit = BindVideoMetadataIdentity(edit, transaction);
        if (IsPhotoClassificationEdit(edit))
        {
            if (ResolvePhotoClassificationOperation(edit.OperationId,transaction) is not { } reference) return;
            edit=edit with { EntityId=reference };
            PreserveDisplacedPhotoClassification(edit,transaction);
        }
        if (IsVideoMetadataEdit(edit) && canonicalize)
        {
            var target = ResolveVideoEditTarget(edit, transaction);
            if (target is not null) edit = edit with { EntityId = target.Value.ReferenceId };
            else
            {
                using var occupied = Command("SELECT video_content_identity FROM metadata_edits WHERE kind=$kind AND entity_id=$id AND field=$field");
                if (transaction is not null) occupied.Transaction = transaction; occupied.Parameters.AddWithValue("$kind", edit.Kind);
                occupied.Parameters.AddWithValue("$id", edit.EntityId); occupied.Parameters.AddWithValue("$field", edit.Field);
                if (occupied.ExecuteScalar() is string identity && identity != edit.VideoContentIdentity) return;
            }
        }
        using var command = Command("""
            INSERT INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id,video_content_identity,server_version,base_version)
            VALUES($kind,$id,$field,$value,$utc,$op,$identity,$version,$base)
            ON CONFLICT(kind,entity_id,field) DO UPDATE SET value=excluded.value,updated_utc=excluded.updated_utc,operation_id=excluded.operation_id,video_content_identity=excluded.video_content_identity,
                server_version=excluded.server_version,base_version=excluded.base_version
            WHERE $force=1 OR (excluded.server_version>0 AND excluded.server_version>metadata_edits.server_version)
               OR (excluded.server_version=0 AND (excluded.updated_utc > metadata_edits.updated_utc
               OR (excluded.updated_utc=metadata_edits.updated_utc AND excluded.operation_id>metadata_edits.operation_id)))
            """);
        if (transaction is not null) command.Transaction = transaction;
        command.Parameters.AddWithValue("$kind", edit.Kind); command.Parameters.AddWithValue("$id", edit.EntityId);
        command.Parameters.AddWithValue("$field", edit.Field); command.Parameters.AddWithValue("$value", edit.Value);
        command.Parameters.AddWithValue("$utc", edit.UpdatedUtc); command.Parameters.AddWithValue("$op", edit.OperationId);
        command.Parameters.AddWithValue("$identity", (object?)edit.VideoContentIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", edit.ServerVersion); command.Parameters.AddWithValue("$base", edit.BaseVersion);
        command.Parameters.AddWithValue("$force", force ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<MetadataEdit> GetMetadataEdits(SqliteTransaction? transaction = null)
        => EffectiveMetadataEdits(ReadMetadataEdits(transaction), transaction);

    public IReadOnlyList<MetadataEdit> GetPendingMetadataEdits(SqliteTransaction? transaction=null)
        => EffectiveMetadataEdits(ReadMetadataEdits(transaction,pendingOnly:true),transaction)
            .Where(edit=>!(IsVideoMetadataEdit(edit) || IsPhotoClassificationEdit(edit)) || FindMetadataEdit(edit,transaction)?.OperationId==edit.OperationId).ToArray();

    public IReadOnlyList<MetadataEdit> GetVideoMetadataEdits(SqliteTransaction? transaction=null)
        => EffectiveMetadataEdits(ReadMetadataEdits(transaction,videosOnly:true),transaction);

    private IReadOnlyList<MetadataEdit> ReadMetadataEdits(SqliteTransaction? transaction = null,bool pendingOnly=false,bool videosOnly=false,bool facesOnly=false)
    {
        var predicates=new List<string>();
        if (pendingOnly) predicates.Add("server_version=0");
        if (videosOnly) predicates.Add("kind IN ('video-tag-link','video-concept')");
        if (facesOnly) predicates.Add("kind='face-state'");
        using var command = Command("SELECT kind,entity_id,field,value,updated_utc,operation_id,video_content_identity,server_version,base_version FROM metadata_edits"+
            (predicates.Count>0?" WHERE "+string.Join(" AND ",predicates):"")+" ORDER BY kind,entity_id,field");
        if (transaction is not null) command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        var result = new List<MetadataEdit>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)) { VideoContentIdentity = reader.IsDBNull(6) ? null : reader.GetString(6), ServerVersion = reader.GetInt64(7), BaseVersion = reader.GetInt64(8) });
        return result.Select(edit=>NormalizeCausalPhotoEdit(edit,transaction)).Select(edit => edit.Kind=="video-concept"
            ? edit with { Field=System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonSerializer.Deserialize<string[]>(edit.Field)) } : NormalizeFaceManualEdit(edit)).ToArray();
    }

    public IReadOnlyList<MetadataEdit> MergeMetadataEdits(IReadOnlyList<MetadataEdit> edits, bool applyOnlyChangedFields = false)
    {
        using var transaction = BeginTransaction();
        var result = MergeMetadataEditsInTransaction(edits, transaction, applyOnlyChangedFields);
        transaction.Commit();
        return result;
    }

    public IReadOnlyList<MetadataEdit> MergeMetadataEditsInTransaction(IReadOnlyList<MetadataEdit> edits, SqliteTransaction transaction,
        bool applyOnlyChangedFields=false,string? importedPublisher=null)
    {
        var accepted = new List<MetadataEdit>();
        foreach (var edit in edits)
        {
            ValidateMetadataEdit(edit);
            if (IsArchivedVideoMetadataOperation(edit, transaction)) continue;
            if (IsVideoMetadataEdit(edit) && edit.VideoContentIdentity?.StartsWith("local:", StringComparison.Ordinal) == true
                && ResolveVideoEditTarget(edit, transaction) is null) continue;
            accepted.Add(edit);
            if (IsVideoMetadataEdit(edit) || IsPhotoClassificationEdit(edit) || MetadataEntityExists(edit, transaction))
            {
                var bound = BindVideoMetadataIdentity(NormalizeFaceManualEdit(edit), transaction);
                var current = FindMetadataEdit(bound, transaction);
                if (current is not null)
                {
                    if (bound.ServerVersion > 0 && current.ServerVersion == 0)
                    {
                        if (bound.ServerVersion <= current.BaseVersion) continue;
                        if (current.OperationId != bound.OperationId && current.Value != bound.Value
                            && !IsImportedMetadataOperation(current.OperationId,importedPublisher,transaction))
                            SaveMetadataConflict(current, bound, transaction);
                    }
                    else if (bound.ServerVersion == 0 && current.ServerVersion > 0)
                    {
                        if (current.OperationId != bound.OperationId && current.Value != bound.Value) SaveMetadataConflict(bound, current, transaction);
                        continue;
                    }
                }
                SaveMetadataEdit(bound, transaction);
            }
        }
        var winners = applyOnlyChangedFields
            ? accepted.Select(edit=>FindMetadataEdit(BindVideoMetadataIdentity(edit,transaction),transaction)).OfType<MetadataEdit>().DistinctBy(edit=>edit.OperationId).ToArray()
            : GetMetadataEdits(transaction);
        foreach (var edit in winners) ApplyMetadataEdit(edit, transaction);
        var keys = accepted.Select(edit => MetadataContentKey(BindVideoMetadataIdentity(edit, transaction), transaction)).Where(key => key is not null).ToHashSet();
        var acknowledged = winners.Where(edit => keys.Contains(MetadataContentKey(edit, transaction))).ToArray();
        return acknowledged;
    }

    private bool IsImportedMetadataOperation(string operation,string? publisher,SqliteTransaction transaction)
    {
        if(publisher is null) return false;
        using var query=Command("SELECT 1 FROM sync_imported_metadata_edits WHERE publisher_id=$publisher AND operation_id=$operation");
        query.Transaction=transaction;query.Parameters.AddWithValue("$publisher",publisher);query.Parameters.AddWithValue("$operation",operation);
        return query.ExecuteScalar() is not null;
    }

    public void ReplayMetadataEdits()
    {
        using var transaction = BeginTransaction();
        foreach (var edit in GetMetadataEdits(transaction)) ApplyMetadataEdit(edit, transaction);
        transaction.Commit();
    }

    public void ReplayMetadataEditsInTransaction(SqliteTransaction transaction)
    {
        foreach (var edit in GetMetadataEdits(transaction)) ApplyMetadataEdit(edit, transaction);
    }

    private bool MetadataEntityExists(MetadataEdit edit, SqliteTransaction transaction)
    {
        if(edit.Kind=="scene-description")return SceneMetadataEntityExists(edit,transaction);
        if (IsPhotoClassificationEdit(edit)) return ResolvePhotoClassificationOperation(edit.OperationId,transaction) is not null;
        if (edit.Kind=="space-setting") return edit.EntityId==1;
        if (edit.Kind is "face-state" or "face-rejection" or "video-face-state" or "video-face-rejection")
        {
            var state=edit.Kind is "face-state" or "video-face-state" ? ParseFaceManualState(edit.Value)
                : new FaceManualState(long.Parse(edit.Field,CultureInfo.InvariantCulture),"confirmed",ParseFacePersonRejection(edit.Value).ModelVersion);
            var faceTable=edit.Kind.StartsWith("video-",StringComparison.Ordinal)?"video_effective_faces":"detected_faces";
            using var face=Command($"SELECT 1 FROM {faceTable} WHERE id=$id AND model_version=$model AND ($person IS NULL OR EXISTS(SELECT 1 FROM people WHERE id=$person))");
            face.Transaction=transaction;face.Parameters.AddWithValue("$id",edit.EntityId);face.Parameters.AddWithValue("$model",state.ModelVersion);
            face.Parameters.AddWithValue("$person",(object?)state.PersonId??DBNull.Value);return face.ExecuteScalar() is not null;
        }
        var table = edit.Kind switch { "person" => "people", "tag-link" => "images", "album-link" => "albums", "video-tag-link" or "video-concept" => "videos", _ => throw new ArgumentException("invalid_metadata_kind") };
        var target = IsVideoMetadataEdit(edit) ? ResolveVideoEditTarget(edit, transaction)?.ReferenceId : edit.EntityId;
        if (target is null) return false;
        using var command = Command($"SELECT 1 FROM {table} WHERE id=$id");
        if (transaction is not null) command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", target.Value);
        if (command.ExecuteScalar() is null) return false;
        if (edit.Kind is "person" or "video-concept") return true;
        using var related = Command($"SELECT 1 FROM {(edit.Kind is "tag-link" or "video-tag-link" ? "tags" : "images")} WHERE id=$id");
        if (transaction is not null) related.Transaction = transaction;
        related.Parameters.AddWithValue("$id", long.Parse(edit.Field, CultureInfo.InvariantCulture));
        return related.ExecuteScalar() is not null;
    }

    private void ApplyMetadataEdit(MetadataEdit edit, SqliteTransaction transaction)
    {
        if (!MetadataEntityExists(edit, transaction)) return;
        if(edit.Kind=="scene-description") {ApplySceneDescriptionEdit(edit,transaction);return;}
        if (IsPhotoClassificationEdit(edit)) { ApplyPhotoClassificationEdit(edit,transaction);return; }
        if(edit.Kind=="face-state") { ApplyFaceManualState(edit,transaction);return; }
        if(edit.Kind=="face-rejection") { ApplyFacePersonRejection(edit,transaction);return; }
        if(edit.Kind=="video-face-state") { ApplyVideoFaceManualState(edit,transaction);return; }
        if(edit.Kind=="video-face-rejection") { ApplyVideoFacePersonRejection(edit,transaction);return; }
        if (edit.Kind=="space-setting")
        {
            if (!TryReadClusterPreference(edit.Value,out var preference)) throw new ArgumentException("invalid_cluster_preference");
            // Model-bound preferences are projected directly from their causal
            // field. The legacy singleton must not leak into other models.
            if (preference!.ModelVersion is not null) return;
            using var update=Command("UPDATE semantic_cluster_settings SET automatic=$mode,manual_threshold=$threshold,updated_utc=$now WHERE id=1 AND (automatic IS NOT $mode OR manual_threshold IS NOT $threshold)");
            update.Transaction=transaction; update.Parameters.AddWithValue("$mode",preference!.Automatic);
            update.Parameters.AddWithValue("$threshold",preference.ManualThreshold); update.Parameters.AddWithValue("$now",edit.UpdatedUtc); update.ExecuteNonQuery();
            return;
        }
        if (edit.Kind == "person" && edit.Field.StartsWith("alias:", StringComparison.Ordinal) && edit.Value.Length == 0)
        {
            RemovePersonAliasProjection(edit.EntityId, edit.Field, transaction);
            return;
        }
        if (IsVideoMetadataEdit(edit) && !CanWriteLegacyVideoMetadata(ResolveVideoEditTarget(edit,transaction)!.Value.ReferenceId,transaction)) return;
        if (edit.Kind == "video-concept")
        {
            var key = System.Text.Json.JsonSerializer.Deserialize<string[]>(edit.Field)!;
            using var concept = Command("INSERT INTO video_visual_concepts(video_id,concept_id,model_version,state) VALUES($id,$concept,$model,$state) ON CONFLICT(video_id,concept_id,model_version) DO UPDATE SET state=excluded.state");
            if (transaction is not null) concept.Transaction = transaction;
            concept.Parameters.AddWithValue("$id", ResolveVideoEditTarget(edit, transaction)!.Value.ContentId);
            concept.Parameters.AddWithValue("$concept", key[0]); concept.Parameters.AddWithValue("$model", key[1]);
            concept.Parameters.AddWithValue("$state", edit.Value); concept.ExecuteNonQuery();
            return;
        }
        var sql = edit.Kind switch
        {
            "person" when edit.Field == "name" => "UPDATE people SET display_name=$value,updated_utc=MAX(updated_utc,$utc) WHERE id=$id AND display_name IS NOT $value",
            "person" when edit.Field == "description" => "UPDATE people SET description=$value,updated_utc=MAX(updated_utc,$utc) WHERE id=$id AND description IS NOT $value",
            "person" => "INSERT OR IGNORE INTO person_aliases(person_id,alias,created_utc) VALUES($id,$value,$utc)",
            "tag-link" when edit.Value == "1" => "INSERT OR IGNORE INTO image_tags(image_id,tag_id) VALUES($id,$related)",
            "tag-link" => "DELETE FROM image_tags WHERE image_id=$id AND tag_id=$related",
            "video-tag-link" when edit.Value == "1" => "INSERT OR IGNORE INTO video_tags(video_id,tag_id) VALUES($id,$related)",
            "video-tag-link" => "DELETE FROM video_tags WHERE video_id=$id AND tag_id=$related",
            "album-link" when edit.Value == "1" => "INSERT OR IGNORE INTO album_images(album_id,image_id,added_utc) VALUES($id,$related,$utc)",
            _ => "DELETE FROM album_images WHERE album_id=$id AND image_id=$related"
        };
        using var command = Command(sql);
        if (transaction is not null) command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", edit.Kind == "video-tag-link" ? ResolveVideoEditTarget(edit, transaction)!.Value.ContentId : edit.EntityId);
        command.Parameters.AddWithValue("$value", edit.Value);
        command.Parameters.AddWithValue("$utc", edit.UpdatedUtc);
        command.Parameters.AddWithValue("$related", edit.Kind == "person" ? 0 : long.Parse(edit.Field, CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public static void ValidateMetadataEdit(MetadataEdit edit)
    {
        if (edit is null || edit.Kind is null || edit.Field is null || edit.Value is null)
            throw new ArgumentException("invalid_metadata_edit");
        var aliasRemoval = edit.Value.Length == 0 && edit.Field.StartsWith("alias:", StringComparison.Ordinal) &&
            edit.Field.Length == 70 && edit.Field[6..].All(character => char.IsAsciiHexDigit(character) && !char.IsLower(character));
        var person = edit.Kind == "person" && (edit.Field is "name" or "description" || edit.Field == AliasField(edit.Value) || aliasRemoval);
        var link = edit.Kind is "tag-link" or "album-link" or "video-tag-link" && long.TryParse(edit.Field, out var related) && related > 0 && edit.Value is "0" or "1";
        var concept = false;
        if (edit.Kind == "video-concept" && edit.Value is "accepted" or "suggested" or "rejected")
        {
            try { var key = System.Text.Json.JsonSerializer.Deserialize<string[]>(edit.Field); concept = key is { Length: 2 } && key.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 256); }
            catch (System.Text.Json.JsonException) { }
        }
        if (edit.VideoContentIdentity is { } identity && (identity.Length > 160 ||
            !(identity.StartsWith("sha256:", StringComparison.Ordinal) && identity.Length == 71 && identity[7..].All(Uri.IsHexDigit)
              || identity.StartsWith("local:", StringComparison.Ordinal) || identity.StartsWith("orphan:", StringComparison.Ordinal))))
            throw new ArgumentException("invalid_video_content_identity");
        var preference=edit.Kind=="space-setting" && edit.EntityId==1 && TryReadClusterPreference(edit.Value,out var clusterPreference) &&
            edit.Field==ClusterPreferenceField(clusterPreference!.ModelVersion);
        var photo=IsPhotoClassificationEdit(edit) && !string.IsNullOrWhiteSpace(edit.Field) && edit.Field.Length<=256 &&
            (edit.Kind=="photo-classification-confirmation" ? edit.Value is "0" or "1" : edit.Value is "accepted" or "rejected" or "restored" or "retracted");
        if(edit.PhotoContentIdentity is { } photoIdentity && (!photo || !ValidPhotoContentIdentity(photoIdentity)))
            throw new ArgumentException("invalid_photo_content_identity");
        var face=edit.Kind is "face-state" or "video-face-state" && edit.Field=="state";
        if(face) _=ParseFaceManualState(edit.Value);
        if(edit.Kind is "face-rejection" or "video-face-rejection" && long.TryParse(edit.Field,NumberStyles.None,CultureInfo.InvariantCulture,out var rejectedPerson) && rejectedPerson>0 &&
            edit.Field==rejectedPerson.ToString(CultureInfo.InvariantCulture))
        { _=ParseFacePersonRejection(edit.Value);face=true; }
        var scene=edit.Kind=="scene-description" && !string.IsNullOrWhiteSpace(edit.Field) && edit.Field.Length<=256 && TryReadSceneDescriptionText(edit.Value,out _);
        if ((!person && !link && !concept && !preference && !face && !photo && !scene) || edit.EntityId <= 0 || edit.ServerVersion < 0 || edit.BaseVersion < 0 || edit.Value.Length > (scene?65_536:4096) || !Guid.TryParseExact(edit.OperationId, "N", out _)
            || !DateTime.TryParseExact(edit.UpdatedUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) || time.Kind != DateTimeKind.Utc)
            throw new ArgumentException("invalid_metadata_edit");
    }

    public static string AliasField(string alias) => "alias:" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(alias.ToUpperInvariant())));

    internal void RemovePersonAliasProjection(long personId, string field, SqliteTransaction transaction)
    {
        var aliases = new List<string>();
        using (var query = Command("SELECT alias FROM person_aliases WHERE person_id=$id"))
        {
            query.Transaction = transaction; query.Parameters.AddWithValue("$id", personId);
            using var rows = query.ExecuteReader();
            while (rows.Read()) if (AliasField(rows.GetString(0)) == field) aliases.Add(rows.GetString(0));
        }
        foreach (var alias in aliases)
        {
            using var remove = Command("DELETE FROM person_aliases WHERE person_id=$id AND alias=$alias");
            remove.Transaction = transaction; remove.Parameters.AddWithValue("$id", personId);
            remove.Parameters.AddWithValue("$alias", alias); remove.ExecuteNonQuery();
        }
    }
}
