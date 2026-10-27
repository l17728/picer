using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed record MetadataEditConflict(string OperationId, MetadataEdit Proposed, MetadataEdit Current);

public sealed partial class PicerDatabase
{
    public MetadataEdit? ReadCurrentMetadataOperation(string operationId,SqliteTransaction transaction)
    {
        using var query=Command("SELECT request_json FROM metadata_causal_receipts WHERE operation_id=$op");
        query.Transaction=transaction; query.Parameters.AddWithValue("$op",operationId);
        if (query.ExecuteScalar() is not string json) return null;
        var request=JsonSerializer.Deserialize<MetadataEdit>(json)!;
        if (IsArchivedVideoMetadataOperation(request,transaction)) return null;
        return FindMetadataEdit(NormalizeCausalVideoEdit(request,transaction),transaction);
    }

    /// <summary>Only the authoritative space service assigns versions. Clients merge returned versions.</summary>
    public IReadOnlyList<MetadataEdit> AcceptMetadataEdits(IReadOnlyList<MetadataEdit> edits)
    {
        using var transaction = BeginTransaction();
        var result = AcceptMetadataEditsInTransaction(edits, transaction);
        transaction.Commit(); return result;
    }

    public IReadOnlyList<MetadataEdit> AcceptMetadataEditsInTransaction(IReadOnlyList<MetadataEdit> edits, SqliteTransaction transaction,
        CatalogRecreationContext? catalogContext=null)
    {
        var results = new List<MetadataEdit>();
        foreach (var input in edits)
        {
            ValidateMetadataEdit(input);
            if (IsArchivedVideoMetadataOperation(input, transaction)) continue;
            var edit = BindVideoMetadataIdentity(NormalizeFaceManualEdit(input), transaction);
            if (!MetadataEntityExists(edit, transaction)) continue;
            if (IsPhotoClassificationEdit(edit)) edit=NormalizeCausalPhotoEdit(edit,transaction);
            if (IsVideoMetadataEdit(edit)) edit = edit with { EntityId = ResolveVideoEditTarget(edit, transaction)!.Value.ReferenceId };
            var current = FindMetadataEdit(edit, transaction);
            if (current is { ServerVersion: 0 })
            {
                current = current with { ServerVersion = NextMetadataVersion(transaction) };
                SaveMetadataEdit(current, transaction, force: true);
                if (edit.OperationId == current.OperationId && edit.Value == current.Value)
                { results.Add(current); continue; }
            }
            if (current == edit && edit.ServerVersion > 0) { results.Add(current); continue; }
            var request = CausalRequest(edit);
            using (var previous = Command("SELECT request_json,result_json FROM metadata_causal_receipts WHERE operation_id=$op"))
            {
                previous.Transaction = transaction; previous.Parameters.AddWithValue("$op", edit.OperationId);
                using var row = previous.ExecuteReader();
                if (row.Read())
                {
                    var priorRequest = NormalizeCausalVideoEdit(JsonSerializer.Deserialize<MetadataEdit>(row.GetString(0))!,transaction);
                    var priorResult = NormalizeCausalVideoEdit(JsonSerializer.Deserialize<MetadataEdit>(row.GetString(1))!,transaction);
                    if (CausalRequest(priorRequest) != request && CausalRequest(priorResult) != request) throw new InvalidDataException("metadata_operation_payload_changed");
                    results.Add(CausalRequest(priorRequest) == request ? priorResult : current ?? priorResult); continue;
                }
            }
            MetadataEdit accepted;
            var followsRetirement=FollowsConfirmedCatalogRetirement(edit,current,catalogContext,transaction);
            if (!followsRetirement && (edit.ServerVersion > 0 || edit.BaseVersion != (current?.ServerVersion ?? 0)))
            {
                if (current is null) throw new InvalidDataException("metadata_field_baseline_required");
                if (edit.Value != current.Value) SaveMetadataConflict(edit, current, transaction);
                accepted = current;
            }
            else
            {
                accepted = edit with { ServerVersion = NextMetadataVersion(transaction),
                    BaseVersion=followsRetirement?current?.ServerVersion??0:edit.BaseVersion };
                SaveMetadataEdit(accepted, transaction, force: true);
                ApplyMetadataEdit(accepted, transaction);
            }
            using var receipt = Command("INSERT INTO metadata_causal_receipts(operation_id,kind,entity_id,field,request_json,result_json) VALUES($op,$kind,$id,$field,$request,$result)");
            receipt.Transaction = transaction;
            receipt.Parameters.AddWithValue("$op", edit.OperationId); receipt.Parameters.AddWithValue("$kind", edit.Kind);
            receipt.Parameters.AddWithValue("$id", edit.EntityId); receipt.Parameters.AddWithValue("$field", edit.Field);
            receipt.Parameters.AddWithValue("$request", request); receipt.Parameters.AddWithValue("$result", JsonSerializer.Serialize(accepted));
            receipt.ExecuteNonQuery(); results.Add(accepted);
        }
        return results;
    }

    private long NextMetadataVersion(SqliteTransaction transaction)
    {
        using var next = Command("UPDATE metadata_version_clock SET version=MAX(version,(SELECT COALESCE(MAX(server_version),0) FROM metadata_edits))+1 WHERE id=1 RETURNING version");
        next.Transaction = transaction; return Convert.ToInt64(next.ExecuteScalar());
    }

    private static string CausalRequest(MetadataEdit edit) => JsonSerializer.Serialize(IsVideoMetadataEdit(edit) || IsPhotoClassificationEdit(edit) ? edit with { EntityId = 0 } : edit);

    private MetadataEdit NormalizeCausalVideoEdit(MetadataEdit edit,SqliteTransaction? transaction)
    {
        if(edit.Kind=="scene-description")return NormalizeSceneMetadataEdit(edit,transaction);
        if (IsPhotoClassificationEdit(edit)) return NormalizeCausalPhotoEdit(edit,transaction);
        if (!IsVideoMetadataEdit(edit)) return edit;
        using var binding=Command("SELECT content_identity FROM video_metadata_operation_bindings WHERE operation_id=$op");
        if (transaction is not null) binding.Transaction=transaction;
        binding.Parameters.AddWithValue("$op",edit.OperationId);
        if (binding.ExecuteScalar() is string identity) edit=edit with { VideoContentIdentity=identity };
        if (ResolveVideoEditTarget(edit,transaction) is { } target) edit=edit with { EntityId=target.ReferenceId };
        return edit;
    }

    private MetadataEdit? FindMetadataEdit(MetadataEdit edit, SqliteTransaction? transaction)
    {
        edit=NormalizeSceneMetadataEdit(edit,transaction);
        var identityPredicate = IsPhotoClassificationEdit(edit)
            ? "(entity_id=$id OR EXISTS(SELECT 1 FROM photo_metadata_operation_bindings b WHERE b.operation_id=metadata_edits.operation_id AND b.content_identity=$identity))"
            : IsVideoMetadataEdit(edit) ? "(entity_id=$id OR video_content_identity=$identity)" : "entity_id=$id";
        var fieldPredicate=edit.Kind=="video-concept"
            ? "(field=$field OR (json_extract(field,'$[0]')=json_extract($field,'$[0]') AND json_extract(field,'$[1]')=json_extract($field,'$[1]')))" : "field=$field";
        using var query = Command($"""
            SELECT kind,entity_id,field,value,updated_utc,operation_id,video_content_identity,server_version,base_version
            FROM metadata_edits WHERE kind=$kind AND {fieldPredicate} AND {identityPredicate}
            """);
        if (transaction is not null) query.Transaction = transaction;
        query.Parameters.AddWithValue("$kind", edit.Kind); query.Parameters.AddWithValue("$field", edit.Field);
        query.Parameters.AddWithValue("$id", edit.EntityId);
        if (IsVideoMetadataEdit(edit)) query.Parameters.AddWithValue("$identity", (object?)edit.VideoContentIdentity ?? DBNull.Value);
        if (IsPhotoClassificationEdit(edit)) query.Parameters.AddWithValue("$identity", (object?)edit.PhotoContentIdentity ?? DBNull.Value);
        var candidates = new List<MetadataEdit>();
        using (var rows = query.ExecuteReader())
            while (rows.Read()) candidates.Add(new(rows.GetString(0),rows.GetInt64(1),rows.GetString(2),rows.GetString(3),rows.GetString(4),rows.GetString(5))
                { VideoContentIdentity=rows.IsDBNull(6)?null:rows.GetString(6),ServerVersion=rows.GetInt64(7),BaseVersion=rows.GetInt64(8) });
        var key = MetadataContentKey(edit, transaction);
        return EffectiveMetadataEdits(candidates.Select(candidate=>NormalizeCausalPhotoEdit(candidate,transaction)).Select(candidate=>candidate.Kind=="video-concept"
            ? candidate with { Field=JsonSerializer.Serialize(JsonSerializer.Deserialize<string[]>(candidate.Field)) } : NormalizeFaceManualEdit(candidate)), transaction)
            .FirstOrDefault(candidate => MetadataContentKey(candidate, transaction) == key);
    }

    private void SaveMetadataConflict(MetadataEdit proposed, MetadataEdit current, SqliteTransaction transaction)
    {
        using var insert = Command("""
            INSERT INTO metadata_edit_conflicts(operation_id,kind,entity_id,field,proposed_json,current_json)
            VALUES($op,$kind,$id,$field,$proposed,$current) ON CONFLICT(operation_id) DO NOTHING
            """);
        insert.Transaction = transaction;
        insert.Parameters.AddWithValue("$op", proposed.OperationId); insert.Parameters.AddWithValue("$kind", proposed.Kind);
        insert.Parameters.AddWithValue("$id", proposed.EntityId); insert.Parameters.AddWithValue("$field", proposed.Field);
        insert.Parameters.AddWithValue("$proposed", JsonSerializer.Serialize(proposed)); insert.Parameters.AddWithValue("$current", JsonSerializer.Serialize(current));
        insert.ExecuteNonQuery();
    }

    public IReadOnlyList<MetadataEditConflict> GetMetadataEditConflicts(SqliteTransaction? transaction = null)
    {
        using var query = Command("SELECT operation_id,proposed_json,current_json FROM metadata_edit_conflicts WHERE state='pending' ORDER BY rowid LIMIT 500");
        if (transaction is not null) query.Transaction = transaction;
        var result = new List<MetadataEditConflict>();
        using (var rows = query.ExecuteReader())
            while (rows.Read()) result.Add(new(rows.GetString(0),JsonSerializer.Deserialize<MetadataEdit>(rows.GetString(1))!,JsonSerializer.Deserialize<MetadataEdit>(rows.GetString(2))!));
        return result.Select(conflict =>
        {
            var proposed=NormalizeCausalVideoEdit(conflict.Proposed,transaction);
            return conflict with { Proposed=proposed,Current=FindMetadataEdit(proposed,transaction)??NormalizeCausalVideoEdit(conflict.Current,transaction) };
        }).ToArray();
    }

    public void ResolveMetadataEditConflict(string operationId, bool useProposed, long expectedVersion, string expectedOperationId,
        bool authoritative = false, string? expectedEpoch = null)
    {
        if (!Guid.TryParseExact(operationId, "N", out _) || !Guid.TryParseExact(expectedOperationId, "N", out _) || expectedVersion < 0)
            throw new ArgumentException("invalid_metadata_resolution");
        var resolution = JsonSerializer.Serialize(new MetadataConflictResolution(operationId,useProposed,expectedVersion,expectedOperationId,expectedEpoch));
        using var transaction = BeginTransaction();
        if (authoritative)
        {
            using var epoch = Command("SELECT epoch FROM sync_space_state WHERE id=1 AND state='active'"); epoch.Transaction = transaction;
            if (expectedEpoch is null || epoch.ExecuteScalar() is not string currentEpoch || currentEpoch != expectedEpoch)
                throw new InvalidOperationException("space_epoch_reset_required");
        }
        MetadataEdit proposed;
        using (var query = Command("SELECT proposed_json,state,resolution_json FROM metadata_edit_conflicts WHERE operation_id=$op"))
        {
            query.Transaction = transaction; query.Parameters.AddWithValue("$op", operationId);
            using var row = query.ExecuteReader();
            if (!row.Read()) throw new InvalidOperationException("metadata_conflict_missing");
            if (row.GetString(1) != "pending")
            {
                if (!row.IsDBNull(2) && row.GetString(2) == resolution) return;
                throw new InvalidOperationException("metadata_conflict_resolution_changed");
            }
            proposed = JsonSerializer.Deserialize<MetadataEdit>(row.GetString(0))!;
        }
        proposed = NormalizeCausalVideoEdit(proposed,transaction);
        var current = FindMetadataEdit(proposed, transaction);
        if (current is null || Math.Max(current.ServerVersion,current.BaseVersion) != expectedVersion || current.OperationId != expectedOperationId)
            throw new InvalidOperationException("metadata_conflict_changed");
        if (authoritative && current.ServerVersion == 0)
            current = AcceptMetadataEditsInTransaction([current],transaction).Single();
        if (useProposed)
        {
            if (!MetadataEntityExists(proposed, transaction)) throw new InvalidOperationException("metadata_conflict_entity_unavailable");
            if (authoritative)
            {
                if (current.ServerVersion == 0) current = AcceptMetadataEditsInTransaction([current],transaction).Single();
                current = AcceptMetadataEditsInTransaction([proposed with { OperationId=Guid.NewGuid().ToString("N"),
                    UpdatedUtc=DateTime.UtcNow.ToString("O"),ServerVersion=0,BaseVersion=current.ServerVersion }],transaction).Single();
            }
            else
            {
                RecordMetadataEdit(proposed.Kind, proposed.EntityId, proposed.Field, proposed.Value, transaction);
                ReplayMetadataEditsInTransaction(transaction);
            }
        }
        using var resolve = Command("UPDATE metadata_edit_conflicts SET state='resolved',resolution_json=$resolution,resolved_version=$version WHERE operation_id=$op");
        resolve.Transaction = transaction; resolve.Parameters.AddWithValue("$op", operationId);
        resolve.Parameters.AddWithValue("$version",authoritative ? current.ServerVersion : DBNull.Value);
        resolve.Parameters.AddWithValue("$resolution",resolution); resolve.ExecuteNonQuery();
        if (authoritative)
        {
            // A keep-current choice changes no business field, so its receipt
            // needs an explicit notification just as much as an applied edit.
            var kind=proposed.Kind switch { "person"=>"person", "face-state" or "face-rejection"=>"face", "video-face-state" or "video-face-rejection"=>"video-face", "tag-link" or "photo-classification-confirmation" or "photo-classification-policy"=>"image", "album-link"=>"album", _=>"video" };
            using var notify=Command(proposed.Kind=="space-setting" ? """
                INSERT INTO sync_publisher_notifications(publisher_id,revision)
                SELECT publisher_id,1 FROM sync_publishers WHERE state='receiving'
                ON CONFLICT(publisher_id) DO UPDATE SET revision=revision+1
                """ : """
                INSERT INTO sync_publisher_notifications(publisher_id,revision)
                SELECT DISTINCT publisher_id,1 FROM sync_entity_identities WHERE kind=$kind AND target_id=$id
                ON CONFLICT(publisher_id) DO UPDATE SET revision=revision+1
                """);
            notify.Transaction=transaction; notify.Parameters.AddWithValue("$kind",kind); notify.Parameters.AddWithValue("$id",proposed.EntityId);
            notify.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
