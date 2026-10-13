using Picer.Core.Data;

namespace Picer.IndexService.Identity;

public sealed class SpacePurgeCoordinator(PicerDatabase database, RestoreGuard guard, string spaceId)
{
    public void Recover()
    {
        var current = Generation();
        var protectedState = guard.Read(spaceId);
        var pending = ReadPending();
        if (pending is null)
        {
            if (protectedState.Generation == current && protectedState.Pending) guard.CompletePurge(spaceId, current);
            guard.VerifyCurrent(spaceId, current);
            return;
        }
        if (pending.Generation != current + 1) throw new InvalidDataException("purge_journal_generation_mismatch");
        if (protectedState.Generation == current && !protectedState.Pending)
            _ = guard.PreparePurge(spaceId, current);
        else if (protectedState.Generation != pending.Generation || !protectedState.Pending)
            throw new InvalidDataException("purge_guard_recovery_mismatch");
        Apply(pending);
    }

    public string Purge(string kind, long id)
    {
        if (kind is not ("image" or "video") || id <= 0) throw new ArgumentException("invalid_purge_target");
        Recover();
        using (var exists = database.Command(kind == "image" ? "SELECT 1 FROM images WHERE id=$id" : "SELECT 1 FROM videos WHERE id=$id"))
        {
            exists.Parameters.AddWithValue("$id", id);
            if (exists.ExecuteScalar() is null) return Epoch();
        }
        var current = Generation();
        var pending = new Pending(Guid.NewGuid().ToString("N"), kind, id, checked(current + 1));
        using (var transaction = database.BeginTransaction())
        {
            using var journal = database.Command("INSERT INTO sync_purge_journal(id,operation_id,media_kind,media_id,generation) VALUES(1,$operation,$kind,$id,$generation)");
            journal.Parameters.AddWithValue("$operation", pending.Operation); journal.Parameters.AddWithValue("$kind", kind);
            journal.Parameters.AddWithValue("$id", id); journal.Parameters.AddWithValue("$generation", pending.Generation); journal.ExecuteNonQuery();
            using var fence = database.Command("UPDATE sync_space_state SET state='fencing' WHERE id=1"); fence.ExecuteNonQuery();
            transaction.Commit();
        }
        _ = guard.PreparePurge(spaceId, current);
        Apply(pending);
        return Epoch();
    }

    private void Apply(Pending pending)
    {
        using (var transaction = database.BeginTransaction())
        {
            if (pending.Kind == "image")
            {
                using var mappings = database.Command("""
                    DELETE FROM sync_entity_identities WHERE kind='image' AND target_id=$id;
                    DELETE FROM sync_entity_identities WHERE kind='face' AND target_id IN (SELECT id FROM detected_faces WHERE image_id=$id);
                    """);
                mappings.Parameters.AddWithValue("$id", pending.Id); mappings.ExecuteNonQuery();
                _ = new ImageRecordPurger(database).Purge(pending.Id, requireMissing: false, transactionHeld: true);
            }
            else
            {
                using var mappings = database.Command("""
                    DELETE FROM sync_entity_identities WHERE kind='video' AND target_id=$id;
                    DELETE FROM sync_entity_identities WHERE kind='keyframe' AND target_id IN (
                        SELECT id FROM video_keyframes WHERE video_id=(SELECT content_id FROM videos WHERE id=$id)
                        AND (SELECT COUNT(*) FROM videos WHERE content_id=(SELECT content_id FROM videos WHERE id=$id))=1);
                    DELETE FROM sync_entity_identities WHERE kind='video-face' AND target_id IN (
                        SELECT id FROM video_detected_faces WHERE video_id=(SELECT content_id FROM videos WHERE id=$id)
                        AND (SELECT COUNT(*) FROM videos WHERE content_id=(SELECT content_id FROM videos WHERE id=$id))=1);
                    """);
                mappings.Parameters.AddWithValue("$id", pending.Id); mappings.ExecuteNonQuery();
                _ = new VideoRecordPurger(database, allowSourceCacheCleanup: false).Purge(pending.Id, transactionHeld: true);
            }
            using var reset = database.Command("""
                DELETE FROM sync_outbox;
                DELETE FROM sync_operation_receipts;
                DELETE FROM sync_exports;
                DELETE FROM mirror_media_changes;
                UPDATE mirror_revision SET epoch=lower(hex(randomblob(16))),revision=0,metadata_revision=metadata_revision+1 WHERE id=1;
                INSERT INTO mirror_media_changes(kind,media_id,source_id,revision,deleted)
                    SELECT 'image',id,source_id,ROW_NUMBER() OVER(ORDER BY id),0 FROM images;
                INSERT INTO mirror_media_changes(kind,media_id,source_id,revision,deleted)
                    SELECT 'video',id,source_id,(SELECT COUNT(*) FROM images)+ROW_NUMBER() OVER(ORDER BY id),0 FROM videos;
                UPDATE mirror_revision SET revision=(SELECT COUNT(*) FROM mirror_media_changes) WHERE id=1;
                UPDATE sync_space_state SET epoch=lower(hex(randomblob(16))),change_sequence=0,purge_generation=$generation,state='active' WHERE id=1;
                UPDATE sync_publishers SET state='rebaseline',epoch=(SELECT epoch FROM sync_space_state WHERE id=1);
                DELETE FROM sync_purge_journal;
                """);
            reset.Parameters.AddWithValue("$generation", pending.Generation); reset.ExecuteNonQuery();
            transaction.Commit();
        }
        guard.CompletePurge(spaceId, pending.Generation);
    }

    private sealed record Pending(string Operation, string Kind, long Id, long Generation);
    private Pending? ReadPending()
    {
        using var command = database.Command("SELECT operation_id,media_kind,media_id,generation FROM sync_purge_journal WHERE id=1");
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetString(1), row.GetInt64(2), row.GetInt64(3)) : null;
    }
    private long Generation() { using var command = database.Command("SELECT purge_generation FROM sync_space_state WHERE id=1"); return Convert.ToInt64(command.ExecuteScalar()); }
    private string Epoch() { using var command = database.Command("SELECT epoch FROM sync_space_state WHERE id=1"); return (string)command.ExecuteScalar()!; }
}
