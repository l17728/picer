using System.Net.Http.Json;
using Picer.Core.Data;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    private async Task<SpacePublisher> RebaselineAsync(PicerDatabase database, string library, DeviceSession session,
        IProgress<string>? progress, CancellationToken token)
    {
        progress?.Report("空间索引已清除过记录，正在核对当前基线；不会修改原始照片和视频…");
        using (var create = database.Command("DROP TABLE IF EXISTS temp.rebaseline_remote; CREATE TEMP TABLE rebaseline_remote(kind TEXT,local_key TEXT,global_id TEXT,PRIMARY KEY(kind,local_key));")) create.ExecuteNonQuery();
        SpacePublisher? publisher = null; var afterKind = ""; var afterKey = "";
        while (true)
        {
            using var response = await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/rebaseline",
                new SpaceRebaselineRequest(library, session.DeviceId, publisher?.Epoch, afterKind, afterKey), token);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<SpaceRebaselinePage>(cancellationToken: token) ?? throw new InvalidDataException("rebaseline_missing");
            SpaceSyncProtocol.RequireMetadataSupport(page.Publisher);
            if (publisher is not null && publisher != page.Publisher || page.Identities.Count > 500 ||
                page.Publisher.LibraryId != library || page.Publisher.IncarnationId != session.DeviceId) throw new InvalidDataException("rebaseline_changed");
            publisher = page.Publisher;
            using (var local = database.Command("SELECT last_sequence FROM sync_publishers WHERE publisher_id=$id AND library_id=$library AND device_grant_id=$grant AND space_id=$space"))
            {
                local.Parameters.AddWithValue("$id", publisher.PublisherId); local.Parameters.AddWithValue("$library", library);
                local.Parameters.AddWithValue("$grant", session.GrantId); local.Parameters.AddWithValue("$space", session.SpaceId);
                if (local.ExecuteScalar() is not long sequence || sequence != publisher.LastSequence)
                    throw new InvalidOperationException("本机同步记录缺失或来自旧备份，已停止上传以避免恢复已清除的索引。请使用当前来源电脑完成同步恢复。");
            }
            using (var transaction = database.BeginTransaction())
            {
                foreach (var item in page.Identities)
                {
                    using var insert = database.Command("INSERT INTO temp.rebaseline_remote VALUES($kind,$key,$global)");
                    insert.Parameters.AddWithValue("$kind", item.Kind); insert.Parameters.AddWithValue("$key", item.LocalId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    insert.Parameters.AddWithValue("$global", item.GlobalId); insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            if (!page.HasMore) break;
            if (afterKind == page.AfterKind && afterKey == page.AfterKey) throw new InvalidDataException("rebaseline_cursor_stalled");
            afterKind = page.AfterKind; afterKey = page.AfterKey;
        }
        var export = LoadExport(database, publisher.PublisherId);
        if (export is not null) DeleteSnapshot(export.Name);
        using (var transaction = database.BeginTransaction())
        {
            var removed = new List<(string Kind, long Id)>();
            using (var missing = database.Command("""
                SELECT kind,local_key FROM sync_entity_identities l WHERE publisher_id=$id AND kind IN ('image','video')
                AND NOT EXISTS(SELECT 1 FROM temp.rebaseline_remote r WHERE r.kind=l.kind AND r.local_key=l.local_key AND r.global_id=l.global_id)
                """))
            {
                missing.Parameters.AddWithValue("$id", publisher.PublisherId);
                using var row = missing.ExecuteReader();
                while (row.Read()) removed.Add((row.GetString(0), long.Parse(row.GetString(1), System.Globalization.CultureInfo.InvariantCulture)));
            }
            foreach (var item in removed)
            {
                if (item.Kind == "image") new ImageRecordPurger(database).Purge(item.Id, requireMissing: false, transactionHeld: true);
                // This database belongs to the source host. Clear its generated
                // source caches too, including recovery after a lost purge reply.
                // The service-side coordinator continues to disable source cleanup.
                else new VideoRecordPurger(database).Purge(item.Id, requireMissing: false, transactionHeld: true);
            }
            using var reset = database.Command("""
                DELETE FROM sync_outbox WHERE publisher_id=$id;
                DELETE FROM sync_exports WHERE publisher_id=$id;
                DELETE FROM sync_operation_receipts WHERE publisher_id=$id;
                DELETE FROM sync_training_baselines WHERE publisher_id=$id;
                DELETE FROM sync_entity_identities WHERE publisher_id=$id AND NOT EXISTS(
                    SELECT 1 FROM temp.rebaseline_remote r WHERE r.kind=sync_entity_identities.kind AND r.local_key=sync_entity_identities.local_key AND r.global_id=sync_entity_identities.global_id)
                    AND NOT EXISTS(SELECT 1 FROM sync_pending_identity_adoptions p WHERE p.publisher_id=$id
                        AND p.kind=sync_entity_identities.kind AND CAST(p.local_id AS TEXT)=sync_entity_identities.local_key
                        AND p.global_id=sync_entity_identities.global_id)
                    AND NOT EXISTS(SELECT 1 FROM sync_catalog_completion_inbox c WHERE c.publisher_id=$id
                        AND c.kind=sync_entity_identities.kind AND c.global_id=sync_entity_identities.global_id);
                DELETE FROM sync_pending_identity_adoptions WHERE publisher_id=$id AND EXISTS(
                    SELECT 1 FROM temp.rebaseline_remote r WHERE r.kind=sync_pending_identity_adoptions.kind
                        AND r.local_key=CAST(sync_pending_identity_adoptions.local_id AS TEXT)
                        AND r.global_id=sync_pending_identity_adoptions.global_id);
                UPDATE sync_publishers SET epoch=$epoch,state='rebaselining',downloaded_sequence=0,downloaded_metadata_version=0,downloaded_metadata_operation='',downloaded_scope_hash='',uploaded_media_revision=-1,uploaded_metadata_revision=-1,uploaded_scope_hash='' WHERE publisher_id=$id;
                UPDATE sync_catalog_completion_inbox SET epoch=$epoch WHERE publisher_id=$id;
                """);
            reset.Parameters.AddWithValue("$id", publisher.PublisherId); reset.Parameters.AddWithValue("$epoch", publisher.Epoch); reset.ExecuteNonQuery();
            transaction.Commit();
        }
        using var complete = await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/rebaseline",
            new SpaceRebaselineRequest(library, session.DeviceId, publisher.Epoch, Complete: true), token);
        complete.EnsureSuccessStatusCode();
        var receipt = await complete.Content.ReadFromJsonAsync<SpaceRebaselinePage>(cancellationToken: token)
            ?? throw new InvalidDataException("rebaseline_receipt_missing");
        if (receipt.Publisher != (publisher with { LastSequence = 0 }) || receipt.Identities is null ||
            receipt.Identities.Count != 0 || receipt.HasMore)
            throw new InvalidDataException("rebaseline_receipt_mismatch");
        using var finish = database.Command("UPDATE sync_publishers SET state='publishing',last_sequence=0 WHERE publisher_id=$id AND epoch=$epoch AND state='rebaselining'");
        finish.Parameters.AddWithValue("$id", publisher.PublisherId);
        finish.Parameters.AddWithValue("$epoch", publisher.Epoch);
        if (finish.ExecuteNonQuery() != 1) throw new InvalidDataException("rebaseline_local_state_changed");
        return publisher with { LastSequence = 0 };
    }
}
