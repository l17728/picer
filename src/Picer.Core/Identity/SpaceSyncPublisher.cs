using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;

namespace Picer.Core.Identity;

/// <summary>Durable sender journal. The exact operation and its identities are saved before HTTP and acknowledged after the server's committed receipt.</summary>
public sealed partial class SpaceSyncPublisher(DeviceAccountClient accountClient, PicerDatabase localDatabase, string snapshotRoot)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _snapshots = Path.GetFullPath(snapshotRoot);

    public async Task<int> PublishAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default, SpaceUploadScope? scope = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var completed = 0;
            while (true)
            {
            using var publisherLease = await AcquirePublisherLeaseAsync(cancellationToken).ConfigureAwait(false);
            using var database = localDatabase.OpenIndependentConnection();
            scope ??= new(database.GetSources().Select(source => source.Id).ToArray(), People: true, PreciseLocation: true);
            var availableSources = database.GetSources().Select(source => source.Id).ToHashSet();
            if (scope.SourceIds.Count == 0 || !scope.Photos && !scope.Videos || scope.SourceIds.Any(id => !availableSources.Contains(id)))
                throw new ArgumentException("请至少选择一个来源目录以及照片或视频索引。");
            var scopeHash = DeviceAuthorization.Digest(JsonSerializer.Serialize(scope with { SourceIds = scope.SourceIds.Distinct().Order().ToArray() }, DeviceAuthorization.Json));
            var session = accountClient.Connection.Session ?? throw new InvalidOperationException("device_login_required");
            if (!session.Scope.Split(' ').Contains("index.write")) throw new UnauthorizedAccessException("此设备尚未获准上传索引，请在网页中重新授权。");
            string library;
            using (var query = database.Command("SELECT library_id FROM library_identity WHERE id=1")) library = (string)query.ExecuteScalar()!;
            using var registration = await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/publishers", new SpacePublisherRequest(library, session.DeviceId), cancellationToken);
            SpacePublisher publisher;
            if (registration.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                using var error = JsonDocument.Parse(await registration.Content.ReadAsStringAsync(cancellationToken));
                if (!error.RootElement.TryGetProperty("error", out var code) || code.GetString() != "publisher_epoch_reset_required") registration.EnsureSuccessStatusCode();
                publisher = await RebaselineAsync(database, library, session, progress, cancellationToken);
            }
            else
            {
                registration.EnsureSuccessStatusCode();
                publisher = await registration.Content.ReadFromJsonAsync<SpacePublisher>(cancellationToken: cancellationToken) ?? throw new InvalidDataException("publisher_response_missing");
            }
            if (publisher.LibraryId != library) throw new InvalidDataException("publisher_library_mismatch");
            EnsureLocalPublisher(database, publisher, session);
            await ResumeCatalogCompletionsAsync(database,publisher,scope,cancellationToken).ConfigureAwait(false);
            if(HasReviewedCatalogReceipt(database,publisher.PublisherId))
                await PullCatalogRetirementsAsync(database,publisher,scope,progress,cancellationToken,reviewedOnly:true).ConfigureAwait(false);
            await AdoptRemoteIdentitiesAsync(database, publisher, scope, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(_snapshots);
            var state = LoadExport(database, publisher.PublisherId);
            if (state is not null && state.ScopeHash != scopeHash)
                throw new InvalidOperationException("仍有上次范围内的未完成同步，请先完成或取消它，再修改上传范围。");
            if (state is { NextBatch: < 0 })
            {
                DeleteSnapshot(state.Name);
                using var discard = database.Command("DELETE FROM sync_exports WHERE publisher_id=$id");
                discard.Parameters.AddWithValue("$id", publisher.PublisherId); discard.ExecuteNonQuery();
                state = null;
            }
            if (state is null)
            {
                using (var unchanged = database.Command("""
                    SELECT COUNT(*) FROM sync_publishers p JOIN mirror_revision r ON r.id=1
                    WHERE p.publisher_id=$id AND p.last_sequence=$sequence AND p.uploaded_media_revision=r.revision
                    AND p.uploaded_metadata_revision=r.metadata_revision AND p.uploaded_scope_hash=$scope AND p.metadata_protocol=$protocol
                    """))
                {
                    unchanged.Parameters.AddWithValue("$id", publisher.PublisherId); unchanged.Parameters.AddWithValue("$sequence", publisher.LastSequence);
                    unchanged.Parameters.AddWithValue("$scope", scopeHash);
                    unchanged.Parameters.AddWithValue("$protocol", SpaceSyncProtocol.MetadataVersion);
                    if (Convert.ToInt64(unchanged.ExecuteScalar()) == 1)
                    {
                        await PushCatalogRetirementsAsync(database,publisher,scope,progress,cancellationToken).ConfigureAwait(false);
                        // Drain an earlier bounded cleanup even when no new snapshot is needed.
                        // Recheck the revision while holding the write transaction: a user edit
                        // between the initial check and this point must not advance the safe cut.
                        using(var cleanupTransaction=database.BeginTransaction())
                        {
                            unchanged.Transaction=cleanupTransaction;
                            if(Convert.ToInt64(unchanged.ExecuteScalar())==1)
                            {
                                using var cut=database.Command("SELECT COALESCE(MAX(sequence),0) FROM sync_metadata_row_journal");
                                CompactDerivedJournal(database,Convert.ToInt64(cut.ExecuteScalar()));
                            }
                            cleanupTransaction.Commit();
                        }
                        progress?.Report("索引自上次成功上传后没有变化，无需重复上传。"); return completed;
                    }
                }
                progress?.Report("正在创建一致的索引快照（不扫描原始照片和视频）…");
                var name = Guid.NewGuid().ToString("N") + ".db";
                var path = SnapshotPath(name);
                database.BackupTo(path);
                using var snapshot = new PicerDatabase(path);
                long revision, metadata;
                using (var cut = snapshot.Command("SELECT revision,metadata_revision FROM mirror_revision WHERE id=1"))
                using (var row = cut.ExecuteReader()) { row.Read(); revision = row.GetInt64(0); metadata = row.GetInt64(1); }
                long after = -1;
                var includeMetadata = true;
                using (var previous = snapshot.Command("SELECT uploaded_media_revision,uploaded_metadata_revision,uploaded_scope_hash,metadata_protocol FROM sync_publishers WHERE publisher_id=$id"))
                {
                    previous.Parameters.AddWithValue("$id", publisher.PublisherId);
                    using var row = previous.ExecuteReader();
                    if (row.Read() && row.GetString(2) == scopeHash && row.GetInt64(0) <= revision && row.GetInt32(3) == SpaceSyncProtocol.MetadataVersion)
                    { after = row.GetInt64(0); includeMetadata = row.GetInt64(1) != metadata; }
                }
                using (var selection = snapshot.Command("""
                    CREATE TABLE sync_export_media(kind TEXT NOT NULL,media_id INTEGER NOT NULL,PRIMARY KEY(kind,media_id));
                    INSERT INTO sync_export_media SELECT kind,media_id FROM mirror_media_changes WHERE $after>=0 AND revision>$after AND deleted=0;
                    INSERT OR IGNORE INTO sync_export_media SELECT 'image',id FROM images WHERE $after<0;
                    INSERT OR IGNORE INTO sync_export_media SELECT 'video',id FROM videos WHERE $after<0;
                    """))
                { selection.Parameters.AddWithValue("$after", after); selection.ExecuteNonQuery(); }
                FilterSnapshot(snapshot, scope);
                if (!ReserveSnapshotIdentities(database, path, publisher.PublisherId, revision, metadata))
                {
                    snapshot.Dispose();
                    DeleteSnapshot(name);
                    progress?.Report("索引在准备期间发生变化，正在更新同步快照…");
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }
                using var save = database.Command("INSERT INTO sync_exports(publisher_id,snapshot_name,next_batch,revision_cut,metadata_cut,created_utc,scope_hash,include_metadata,metadata_protocol) VALUES($id,$name,0,$revision,$metadata,$created,$scope,$include,$protocol)");
                save.Parameters.AddWithValue("$id", publisher.PublisherId); save.Parameters.AddWithValue("$name", name);
                save.Parameters.AddWithValue("$revision", revision); save.Parameters.AddWithValue("$metadata", metadata);
                save.Parameters.AddWithValue("$scope", scopeHash);
                save.Parameters.AddWithValue("$include", includeMetadata ? 1 : 0);
                save.Parameters.AddWithValue("$protocol", SpaceSyncProtocol.MetadataVersion);
                save.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O")); save.ExecuteNonQuery();
                state = new(name, 0, revision, metadata, scopeHash, includeMetadata, SpaceSyncProtocol.MetadataVersion);
            }
            var accepted = 0;
            long journalCut=0;
            var supersededDeletions=new List<string>();
            using (var snapshot = new PicerDatabase(SnapshotPath(state.Name)))
            {
                using(var cut=snapshot.Command("SELECT COALESCE(MAX(sequence),0) FROM sync_metadata_row_journal")) journalCut=Convert.ToInt64(cut.ExecuteScalar());
                var number = 0;
                foreach (var prepared in ExportBatches(snapshot, state.IncludeMetadata, publisher.PublisherId,state.MetadataProtocol>=11,scope,state.MetadataProtocol>=12,state.MetadataProtocol>=13))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (number++ < state.NextBatch) continue;
                    var batch = LoadPending(database, publisher.PublisherId) ?? Capture(database, snapshot, publisher, prepared, number - 1);
                    progress?.Report($"正在上传索引：第 {number} 批，已确认 {accepted} 条…");
                    using var response = await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/batches", batch, DeviceAuthorization.Json, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var receipt = await response.Content.ReadFromJsonAsync<SpaceSyncReceipt>(DeviceAuthorization.Json, cancellationToken)
                        ?? throw new InvalidDataException("sync_receipt_missing");
                    if (receipt.Sequence != batch.Sequence || receipt.OperationId != batch.OperationId) throw new InvalidDataException("sync_receipt_mismatch");
                    Acknowledge(database, publisher.PublisherId, receipt, receipt.Status == 200 ? number : -1);
                    if (receipt.Status != 200) throw new InvalidDataException($"索引批次被服务拒绝，已保存失败结果；错误类型：{receipt.Code}。");
                    accepted += receipt.Accepted;
                }
                if(state.IncludeMetadata && state.MetadataProtocol>=11)
                {
                    using var superseded=snapshot.Command("SELECT operation_id FROM sync_export_superseded_deletions ORDER BY operation_id LIMIT 5000");
                    using var rows=superseded.ExecuteReader();while(rows.Read())supersededDeletions.Add(rows.GetString(0));
                }
            }
            using (var transaction = database.BeginTransaction())
            using (var finish = database.Command("""
                UPDATE sync_publishers SET
                    downloaded_sequence=CASE WHEN uploaded_scope_hash<>$scope OR metadata_protocol<>$protocol THEN 0 ELSE downloaded_sequence END,
                    downloaded_metadata_version=CASE WHEN metadata_protocol<>$protocol THEN 0 ELSE downloaded_metadata_version END,
                    downloaded_metadata_operation=CASE WHEN metadata_protocol<>$protocol THEN '' ELSE downloaded_metadata_operation END,
                    uploaded_media_revision=$media,uploaded_metadata_revision=$metadata,uploaded_scope_hash=$scope,metadata_protocol=$protocol WHERE publisher_id=$id;
                DELETE FROM sync_exports WHERE publisher_id=$id;
                """))
            {
                finish.Parameters.AddWithValue("$id", publisher.PublisherId); finish.Parameters.AddWithValue("$media", state.Revision);
                finish.Parameters.AddWithValue("$metadata", state.MetadataRevision); finish.Parameters.AddWithValue("$scope", scopeHash);
                finish.Parameters.AddWithValue("$protocol", state.MetadataProtocol);
                finish.ExecuteNonQuery();
                AcknowledgeDerivedOperations(database,publisher.PublisherId,supersededDeletions);
                CompactDerivedJournal(database,journalCut);
                transaction.Commit();
            }
            DeleteSnapshot(state.Name);
            completed += accepted;
            if (state.MetadataProtocol != SpaceSyncProtocol.MetadataVersion)
            {
                progress?.Report("上次索引上传已恢复，正在补齐新版本的内容身份映射…");
                continue;
            }
            long deferredAdoptions;
            using (var pending = database.Command("SELECT COUNT(*) FROM sync_pending_identity_adoptions WHERE publisher_id=$id"))
            {
                pending.Parameters.AddWithValue("$id", publisher.PublisherId);
                deferredAdoptions = Convert.ToInt64(pending.ExecuteScalar());
            }
            progress?.Report(deferredAdoptions == 0
                ? $"本次索引快照上传完成，已确认 {accepted} 条记录。"
                : $"当前范围已同步，已确认 {accepted} 条记录；另有 {deferredAdoptions} 项认领暂缓，已保留本机记录，不扩大授权范围或重复上传。");
            await PushCatalogRetirementsAsync(database,publisher,scope,progress,cancellationToken).ConfigureAwait(false);
            return completed;
            }
        }
        finally { _gate.Release(); }
    }

    private static void EnsureLocalPublisher(PicerDatabase database, SpacePublisher publisher, DeviceSession session)
    {
        SpaceSyncProtocol.RequireMetadataSupport(publisher);
        if (publisher.IncarnationId != session.DeviceId || !Guid.TryParseExact(publisher.PublisherId, "N", out _) || !Guid.TryParseExact(publisher.Epoch, "N", out _))
            throw new InvalidDataException("invalid_publisher_identity");
        using var transaction = database.BeginTransaction();
        using var query = database.Command("SELECT epoch,last_sequence,device_grant_id,space_id,state FROM sync_publishers WHERE publisher_id=$id");
        query.Parameters.AddWithValue("$id", publisher.PublisherId);
        using (var row = query.ExecuteReader())
        {
            if (row.Read())
            {
                if (row.GetString(0) != publisher.Epoch || row.GetString(2) != session.GrantId || row.GetString(3) != session.SpaceId)
                    throw new InvalidOperationException("空间或设备授权已变化，需要建立新的同步基线。");
                var completingBaseline = row.GetString(4) == "rebaselining";
                row.Close();
                if (completingBaseline)
                {
                    if (publisher.LastSequence != 0) throw new InvalidOperationException("publisher_epoch_reset_required");
                    using var finish = database.Command("UPDATE sync_publishers SET state='publishing',last_sequence=0 WHERE publisher_id=$id");
                    finish.Parameters.AddWithValue("$id", publisher.PublisherId); finish.ExecuteNonQuery();
                }
                transaction.Commit(); return;
            }
        }
        if (publisher.LastSequence != 0) throw new InvalidOperationException("本机缺少服务端对应的同步记录，请重新配对此设备，避免旧备份覆盖当前空间。");
        using var insert = database.Command("INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch) VALUES($id,$library,$incarnation,$grant,$space,'publishing',$epoch)");
        insert.Parameters.AddWithValue("$id", publisher.PublisherId); insert.Parameters.AddWithValue("$library", publisher.LibraryId);
        insert.Parameters.AddWithValue("$incarnation", publisher.IncarnationId); insert.Parameters.AddWithValue("$grant", session.GrantId);
        insert.Parameters.AddWithValue("$space", session.SpaceId); insert.Parameters.AddWithValue("$epoch", publisher.Epoch); insert.ExecuteNonQuery();
        transaction.Commit();
    }

    private static SpaceSyncBatch? LoadPending(PicerDatabase database, string publisher)
    {
        using var query = database.Command("SELECT payload FROM sync_outbox WHERE publisher_id=$id ORDER BY sequence LIMIT 1");
        query.Parameters.AddWithValue("$id", publisher);
        return query.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<SpaceSyncBatch>(payload, DeviceAuthorization.Json) : null;
    }

    private static SpaceSyncBatch Capture(PicerDatabase database, PicerDatabase snapshot, SpacePublisher publisher, ExportBatch prepared, int batchNumber)
    {
        using var transaction = database.BeginTransaction();
        var identities = new List<SpaceEntityIdentity>();
        if(prepared.Payload is SpaceMetadataBatch metadata)
            foreach(var scene in metadata.Metadata.SceneGroupDescriptionsOrEmpty)
                database.RememberSceneBinding(publisher.PublisherId,scene.Fingerprint,
                    database.GetSceneTargetFingerprint(publisher.PublisherId,scene.Fingerprint)??DeviceAuthorization.Digest(publisher.PublisherId+":"+scene.Fingerprint),true);
        foreach (var (kind, localId) in prepared.Identities.Distinct())
        {
            if(kind is "tag" or "album" or "person")
            {
                using var frozen=snapshot.Command("SELECT global_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND local_key=$local");
                frozen.Parameters.AddWithValue("$publisher",publisher.PublisherId);frozen.Parameters.AddWithValue("$kind",kind);
                frozen.Parameters.AddWithValue("$local",localId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if(frozen.ExecuteScalar() is string original)
                {
                    identities.Add(new(kind,localId,original));
                    continue;
                }
            }
            using var lookup = database.Command("SELECT global_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND local_key=$local");
            lookup.Parameters.AddWithValue("$publisher", publisher.PublisherId); lookup.Parameters.AddWithValue("$kind", kind);
            lookup.Parameters.AddWithValue("$local", localId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var global = lookup.ExecuteScalar() as string;
            if (global is null)
            {
                global = Guid.NewGuid().ToString("N");
                using var insert = database.Command("INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id) VALUES($publisher,$kind,$local,$global)");
                insert.Parameters.AddWithValue("$publisher", publisher.PublisherId); insert.Parameters.AddWithValue("$kind", kind);
                insert.Parameters.AddWithValue("$local", localId.ToString(System.Globalization.CultureInfo.InvariantCulture)); insert.Parameters.AddWithValue("$global", global);
                insert.ExecuteNonQuery();
            }
            identities.Add(new(kind, localId, global));
        }
        using var sequence = database.Command("SELECT last_sequence+1 FROM sync_publishers WHERE publisher_id=$id");
        sequence.Parameters.AddWithValue("$id", publisher.PublisherId);
        var batch = new SpaceSyncBatch(publisher.PublisherId, publisher.Epoch, Convert.ToInt64(sequence.ExecuteScalar()), Guid.NewGuid().ToString("N"),
            prepared.Kind, JsonSerializer.SerializeToElement(prepared.Payload, DeviceAuthorization.Json), identities);
        var references=new List<SpaceEntityIdentity>();
        foreach(var (kind,localId) in SpaceCatalogReferences.Required(batch))
        {
            string? Lookup(PicerDatabase source)
            {
                using var query=source.Command("SELECT global_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND local_key=$local");
                query.Parameters.AddWithValue("$publisher",publisher.PublisherId);query.Parameters.AddWithValue("$kind",kind);
                query.Parameters.AddWithValue("$local",localId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return query.ExecuteScalar() as string;
            }
            // Legacy frozen exports predate identity reservation. They drain under
            // the same publisher lease, before a later snapshot can rotate identities.
            var global=Lookup(snapshot) ?? Lookup(database) ?? throw new InvalidDataException("catalog_reference_identity_missing");
            references.Add(new(kind,localId,global));
        }
        batch=batch with { CatalogReferences=references };
        using var save = database.Command("INSERT INTO sync_outbox(publisher_id,operation_id,kind,entity_key,payload) VALUES($publisher,$operation,$kind,$key,$payload)");
        save.Parameters.AddWithValue("$publisher", publisher.PublisherId); save.Parameters.AddWithValue("$operation", batch.OperationId);
        save.Parameters.AddWithValue("$kind", prepared.Kind); save.Parameters.AddWithValue("$key", batchNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        save.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(batch, DeviceAuthorization.Json)); save.ExecuteNonQuery();
        transaction.Commit(); return batch;
    }

    private static void Acknowledge(PicerDatabase database, string publisher, SpaceSyncReceipt receipt, int nextBatch)
    {
        using var transaction = database.BeginTransaction();
        AcknowledgeDerivedDeletions(database,publisher,receipt);
        if (receipt.Status == 200 && LoadPending(database, publisher) is { Kind: "metadata" } pending)
            TrainingSyncBaseline.Acknowledge(database, publisher,
                pending.Payload.Deserialize<SpaceMetadataBatch>(DeviceAuthorization.Json)!.Metadata);
        using (var record = database.Command("INSERT INTO sync_operation_receipts(publisher_id,sequence,operation_id,payload_hash,status,result) VALUES($publisher,$sequence,$operation,'ack',$status,$result)"))
        {
            record.Parameters.AddWithValue("$publisher", publisher); record.Parameters.AddWithValue("$sequence", receipt.Sequence);
            record.Parameters.AddWithValue("$operation", receipt.OperationId); record.Parameters.AddWithValue("$status", receipt.Status);
            record.Parameters.AddWithValue("$result", JsonSerializer.Serialize(receipt, DeviceAuthorization.Json)); record.ExecuteNonQuery();
        }
        using var acknowledge = database.Command("DELETE FROM sync_outbox WHERE publisher_id=$publisher AND operation_id=$operation; UPDATE sync_publishers SET last_sequence=$sequence WHERE publisher_id=$publisher; UPDATE sync_exports SET next_batch=$next WHERE publisher_id=$publisher;");
        acknowledge.Parameters.AddWithValue("$publisher", publisher); acknowledge.Parameters.AddWithValue("$operation", receipt.OperationId);
        acknowledge.Parameters.AddWithValue("$sequence", receipt.Sequence); acknowledge.Parameters.AddWithValue("$next", nextBatch); acknowledge.ExecuteNonQuery();
        transaction.Commit();
    }

    private sealed record ExportState(string Name, int NextBatch, long Revision, long MetadataRevision, string ScopeHash, bool IncludeMetadata, int MetadataProtocol);
    private static ExportState? LoadExport(PicerDatabase database, string publisher)
    {
        using var query = database.Command("SELECT snapshot_name,next_batch,revision_cut,metadata_cut,scope_hash,include_metadata,metadata_protocol FROM sync_exports WHERE publisher_id=$id");
        query.Parameters.AddWithValue("$id", publisher);
        using var row = query.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetInt32(1), row.GetInt64(2), row.GetInt64(3), row.GetString(4), row.GetInt32(5) != 0, row.GetInt32(6)) : null;
    }
    private string SnapshotPath(string name)
    {
        if (name.Length != 35 || !name.EndsWith(".db", StringComparison.Ordinal) || !Guid.TryParseExact(name[..32], "N", out _)) throw new InvalidDataException("invalid_snapshot_name");
        return Path.Combine(_snapshots, name);
    }
    private void DeleteSnapshot(string name)
    {
        var path = SnapshotPath(name);
        foreach (var suffix in new[] { "", "-wal", "-shm", ".access" }) File.Delete(path + suffix);
    }
    private sealed record ExportBatch(string Kind, object Payload, IReadOnlyList<(string Kind, long Id)> Identities);

    private async Task<IDisposable> AcquirePublisherLeaseAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try { return new FileStream(localDatabase.DatabasePath + ".publisher-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, timeout.Token).ConfigureAwait(false); }
        }
    }
}
