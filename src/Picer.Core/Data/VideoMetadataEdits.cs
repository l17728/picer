using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    private static bool IsVideoMetadataEdit(MetadataEdit edit) => edit.Kind is "video-tag-link" or "video-concept";

    private MetadataEdit BindVideoMetadataIdentity(MetadataEdit edit, SqliteTransaction? transaction)
    {
        if (!IsVideoMetadataEdit(edit)) return edit;
        using var existing = Command("SELECT video_content_identity FROM metadata_edits WHERE kind=$kind AND entity_id=$id AND field=$field AND operation_id=$op");
        if (transaction is not null) existing.Transaction = transaction;
        existing.Parameters.AddWithValue("$kind", edit.Kind); existing.Parameters.AddWithValue("$id", edit.EntityId);
        existing.Parameters.AddWithValue("$field", edit.Field); existing.Parameters.AddWithValue("$op", edit.OperationId);
        // A legacy retry cannot rebind an already observed operation to a reused reference ID.
        using var observed = Command("SELECT content_identity FROM video_metadata_operation_bindings WHERE operation_id=$op");
        if (transaction is not null) observed.Transaction = transaction; observed.Parameters.AddWithValue("$op", edit.OperationId);
        var identity = observed.ExecuteScalar() as string ?? existing.ExecuteScalar() as string ?? edit.VideoContentIdentity;
        if (identity is null)
        {
            using var current = Command("""
                SELECT CASE WHEN length(c.sha256)=64 AND lower(c.sha256) NOT GLOB '*[^0-9a-f]*'
                    THEN 'sha256:'||lower(c.sha256)
                    ELSE 'local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||c.id END
                FROM videos v JOIN video_contents c ON c.id=v.content_id WHERE v.id=$id
                """);
            if (transaction is not null) current.Transaction = transaction; current.Parameters.AddWithValue("$id", edit.EntityId);
            identity = current.ExecuteScalar() as string ?? "orphan:" + edit.OperationId;
        }
        identity = identity.StartsWith("sha256:", StringComparison.Ordinal) ? identity.ToLowerInvariant() : identity;
        using var bind = Command("INSERT INTO video_metadata_operation_bindings(operation_id,reference_id,content_identity) VALUES($op,$id,$identity) ON CONFLICT(operation_id) DO NOTHING");
        if (transaction is not null) bind.Transaction = transaction; bind.Parameters.AddWithValue("$op", edit.OperationId);
        bind.Parameters.AddWithValue("$id", ResolveVideoEditTarget(edit with { VideoContentIdentity = identity }, transaction)?.ReferenceId ?? edit.EntityId); bind.Parameters.AddWithValue("$identity", identity); bind.ExecuteNonQuery();
        return edit with { VideoContentIdentity = identity };
    }

    private (long ContentId, long ReferenceId)? ResolveVideoEditTarget(MetadataEdit edit, SqliteTransaction? transaction)
    {
        if (edit.VideoContentIdentity is not { } identity) return null;
        using var query = identity.StartsWith("sha256:", StringComparison.Ordinal)
            ? Command("SELECT c.id,MIN(v.id) FROM video_contents c JOIN videos v ON v.content_id=c.id WHERE c.sha256=$key GROUP BY c.id")
            : Command("SELECT c.id,MIN(v.id) FROM video_contents c JOIN videos v ON v.content_id=c.id WHERE c.id=$key AND (SELECT epoch FROM mirror_revision WHERE id=1)=$epoch GROUP BY c.id");
        if (transaction is not null) query.Transaction = transaction;
        if (identity.StartsWith("sha256:", StringComparison.Ordinal)) query.Parameters.AddWithValue("$key", identity[7..]);
        else
        {
            var parts = identity.Split(':');
            if (parts.Length != 3 || parts[0] != "local" || !long.TryParse(parts[2], out var content)) return null;
            query.Parameters.AddWithValue("$key", content); query.Parameters.AddWithValue("$epoch", parts[1]);
        }
        using var reader = query.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : null;
    }

    private (string Kind, string Entity, string Field)? MetadataContentKey(MetadataEdit edit, SqliteTransaction? transaction)
    {
        if (!IsVideoMetadataEdit(edit)) return (edit.Kind, "reference:" + edit.EntityId, edit.Field);
        var target = ResolveVideoEditTarget(edit, transaction);
        return target is null ? null : (edit.Kind, "content:" + target.Value.ContentId, edit.Field);
    }

    private IReadOnlyList<MetadataEdit> EffectiveMetadataEdits(IEnumerable<MetadataEdit> edits, SqliteTransaction? transaction)
    {
        var winners = new Dictionary<(string Kind, string Entity, string Field), MetadataEdit>();
        foreach (var edit in edits)
        {
            var key = MetadataContentKey(edit, transaction);
            if (key is null) continue;
            if (!winners.TryGetValue(key.Value, out var previous) || CompareMetadataEdits(edit, previous) > 0)
                winners[key.Value] = edit;
        }
        return winners.Values.Select(edit => IsVideoMetadataEdit(edit)
                ? edit with { EntityId = ResolveVideoEditTarget(edit, transaction)!.Value.ReferenceId } : edit)
            .OrderBy(edit => edit.Kind, StringComparer.Ordinal).ThenBy(edit => edit.EntityId)
            .ThenBy(edit => edit.Field, StringComparer.Ordinal).ToArray();
    }

    private static int CompareMetadataEdits(MetadataEdit left, MetadataEdit right)
    {
        var timestamp = StringComparer.Ordinal.Compare(left.UpdatedUtc, right.UpdatedUtc);
        return timestamp != 0 ? timestamp : StringComparer.Ordinal.Compare(left.OperationId, right.OperationId);
    }

    /// <summary>Call inside the reference mutation transaction, before detaching or deleting it.</summary>
    public void ReassignVideoMetadataEdits(long referenceId)
    {
        foreach (var edit in ReadMetadataEdits().Where(edit => IsVideoMetadataEdit(edit) && edit.EntityId == referenceId))
        {
            var target = ResolveVideoEditTarget(edit, null);
            if (target is null) continue;
            using var survivor = Command("SELECT MIN(id) FROM videos WHERE content_id=$content AND id<>$id");
            survivor.Parameters.AddWithValue("$content", target.Value.ContentId); survivor.Parameters.AddWithValue("$id", referenceId);
            if (survivor.ExecuteScalar() is long id) SaveMetadataEdit(edit with { EntityId = id }, null, canonicalize: false);
        }
        var contentId = new VideoContentRepository(this).GetContentId(referenceId);
        var bindings = new List<(string Operation, string Identity)>();
        using (var query = Command("""
            SELECT operation_id,content_identity FROM video_metadata_operation_bindings
            WHERE reference_id=$id
                OR content_identity='sha256:'||(SELECT lower(sha256) FROM video_contents WHERE id=$content)
                OR content_identity='local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||$content
            """))
        {
            query.Parameters.AddWithValue("$id", referenceId); query.Parameters.AddWithValue("$content", contentId);
            using var reader = query.ExecuteReader(); while (reader.Read()) bindings.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var binding in bindings)
        {
            var target = ResolveVideoEditTarget(new("video-concept", referenceId, "", "", "", binding.Operation) { VideoContentIdentity = binding.Identity }, null);
            using var survivor = Command("SELECT MIN(id) FROM videos WHERE content_id=$content AND id<>$id");
            survivor.Parameters.AddWithValue("$content", target?.ContentId ?? -1); survivor.Parameters.AddWithValue("$id", referenceId);
            var survivingReference = survivor.ExecuteScalar();
            using var update = survivingReference is long
                ? Command("UPDATE video_metadata_operation_bindings SET reference_id=$survivor WHERE operation_id=$op")
                : Command("DELETE FROM video_metadata_operation_bindings WHERE operation_id=$op");
            if (survivingReference is long id) update.Parameters.AddWithValue("$survivor", id);
            update.Parameters.AddWithValue("$op", binding.Operation); update.ExecuteNonQuery();
        }
        using var remove = Command("DELETE FROM metadata_edits WHERE entity_id=$id AND kind IN ('video-tag-link','video-concept')");
        remove.Parameters.AddWithValue("$id", referenceId); remove.ExecuteNonQuery();
    }
}
