using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed record MetadataEdit(string Kind, long EntityId, string Field, string Value, string UpdatedUtc, string OperationId)
{
    public string? VideoContentIdentity { get; init; }
}

public sealed partial class PicerDatabase
{
    // Per-field last-writer-wins register. Equal timestamps use the operation ID,
    // so every client chooses the same winner regardless of delivery order.
    public void RecordMetadataEdit(string kind, long id, string field, string value, SqliteTransaction transaction)
    {
        using var latest = Command("SELECT updated_utc FROM metadata_edits WHERE kind=$kind AND entity_id=$id AND field=$field");
        if (transaction is not null) latest.Transaction = transaction;
        latest.Parameters.AddWithValue("$kind", kind); latest.Parameters.AddWithValue("$id", id); latest.Parameters.AddWithValue("$field", field);
        var now = DateTime.UtcNow;
        if (latest.ExecuteScalar() is string previous && DateTime.Parse(previous, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) >= now)
            now = DateTime.Parse(previous, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddTicks(1);
        var operation = new MetadataEdit(kind, id, field, value, now.ToString("O"), Guid.NewGuid().ToString("N"));
        if (IsVideoMetadataEdit(operation))
        {
            operation = BindVideoMetadataIdentity(operation, transaction);
            var key = MetadataContentKey(operation, transaction);
            var winner = GetMetadataEdits(transaction).FirstOrDefault(edit => MetadataContentKey(edit, transaction) == key);
            if (winner is not null && DateTime.Parse(winner.UpdatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) >= now)
                now = DateTime.Parse(winner.UpdatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddTicks(1);
        }
        SaveMetadataEdit(operation with { UpdatedUtc = now.ToString("O") }, transaction);
    }

    private void SaveMetadataEdit(MetadataEdit edit, SqliteTransaction? transaction, bool canonicalize = true)
    {
        ValidateMetadataEdit(edit);
        edit = BindVideoMetadataIdentity(edit, transaction);
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
            INSERT INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id,video_content_identity)
            VALUES($kind,$id,$field,$value,$utc,$op,$identity)
            ON CONFLICT(kind,entity_id,field) DO UPDATE SET value=excluded.value,updated_utc=excluded.updated_utc,operation_id=excluded.operation_id,video_content_identity=excluded.video_content_identity
            WHERE excluded.updated_utc > metadata_edits.updated_utc
               OR (excluded.updated_utc=metadata_edits.updated_utc AND excluded.operation_id>metadata_edits.operation_id)
            """);
        if (transaction is not null) command.Transaction = transaction;
        command.Parameters.AddWithValue("$kind", edit.Kind); command.Parameters.AddWithValue("$id", edit.EntityId);
        command.Parameters.AddWithValue("$field", edit.Field); command.Parameters.AddWithValue("$value", edit.Value);
        command.Parameters.AddWithValue("$utc", edit.UpdatedUtc); command.Parameters.AddWithValue("$op", edit.OperationId);
        command.Parameters.AddWithValue("$identity", (object?)edit.VideoContentIdentity ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<MetadataEdit> GetMetadataEdits(SqliteTransaction? transaction = null)
        => EffectiveMetadataEdits(ReadMetadataEdits(transaction), transaction);

    private IReadOnlyList<MetadataEdit> ReadMetadataEdits(SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT kind,entity_id,field,value,updated_utc,operation_id,video_content_identity FROM metadata_edits ORDER BY kind,entity_id,field");
        if (transaction is not null) command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        var result = new List<MetadataEdit>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)) { VideoContentIdentity = reader.IsDBNull(6) ? null : reader.GetString(6) });
        return result;
    }

    public IReadOnlyList<MetadataEdit> MergeMetadataEdits(IReadOnlyList<MetadataEdit> edits)
    {
        using var transaction = BeginTransaction();
        var result = MergeMetadataEditsInTransaction(edits, transaction);
        transaction.Commit();
        return result;
    }

    public IReadOnlyList<MetadataEdit> MergeMetadataEditsInTransaction(IReadOnlyList<MetadataEdit> edits, SqliteTransaction transaction)
    {
        var accepted = new List<MetadataEdit>();
        foreach (var edit in edits)
        {
            ValidateMetadataEdit(edit);
            if (IsVideoMetadataEdit(edit) && edit.VideoContentIdentity?.StartsWith("local:", StringComparison.Ordinal) == true
                && ResolveVideoEditTarget(edit, transaction) is null) continue;
            accepted.Add(edit);
            if (IsVideoMetadataEdit(edit) || MetadataEntityExists(edit, transaction)) SaveMetadataEdit(edit, transaction);
        }
        var winners = GetMetadataEdits(transaction);
        foreach (var edit in winners) ApplyMetadataEdit(edit, transaction);
        var keys = accepted.Select(edit => MetadataContentKey(BindVideoMetadataIdentity(edit, transaction), transaction)).Where(key => key is not null).ToHashSet();
        var acknowledged = winners.Where(edit => keys.Contains(MetadataContentKey(edit, transaction))).ToArray();
        return acknowledged;
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
            "person" when edit.Field == "name" => "UPDATE people SET display_name=$value,updated_utc=$utc WHERE id=$id",
            "person" when edit.Field == "description" => "UPDATE people SET description=$value,updated_utc=$utc WHERE id=$id",
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

    private static void ValidateMetadataEdit(MetadataEdit edit)
    {
        var person = edit.Kind == "person" && (edit.Field is "name" or "description" || edit.Field == AliasField(edit.Value));
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
        if ((!person && !link && !concept) || edit.EntityId <= 0 || edit.Value.Length > 4096 || !Guid.TryParseExact(edit.OperationId, "N", out _)
            || !DateTime.TryParseExact(edit.UpdatedUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) || time.Kind != DateTimeKind.Utc)
            throw new ArgumentException("invalid_metadata_edit");
    }

    public static string AliasField(string alias) => "alias:" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(alias.ToUpperInvariant())));
}
