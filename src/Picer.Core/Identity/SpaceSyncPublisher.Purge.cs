using System.Net.Http.Json;
using Picer.Core.Gateway;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    /// <summary>Explicit user purge, serialized with frozen publication. Uses the
    /// publisher's stable identity, never a cross-device path/hash search.</summary>
    public async Task<bool> PurgePublishedReferenceAsync(string kind,long localId,SpaceUploadScope scope,CancellationToken cancellationToken=default)
    {
        if(kind is not ("image" or "video") || localId<=0) throw new ArgumentException("invalid_media_purge");
        using(var database=localDatabase.OpenIndependentConnection())
        {
            using var media=database.Command($"SELECT source_id FROM {(kind=="image"?"images":"videos")} WHERE id=$id");
            media.Parameters.AddWithValue("$id",localId);
            if(media.ExecuteScalar() is not long source) return true;
            if(!scope.SourceIds.Contains(source) || kind=="image"&&!scope.Photos || kind=="video"&&!scope.Videos)
            {
                using var published=database.Command("SELECT 1 FROM sync_entity_identities WHERE kind=$kind AND local_key=CAST($id AS TEXT) LIMIT 1");
                published.Parameters.AddWithValue("$kind",kind);published.Parameters.AddWithValue("$id",localId);
                if(published.ExecuteScalar() is not null) throw new InvalidOperationException("此来源的照片空间同步已暂停。请先恢复该来源授权，再同步清除索引。");
                return false;
            }
        }
        var session=accountClient.Connection.Session??throw new InvalidOperationException("device_login_required");
        if(!session.Scope.Split(' ').Contains("index.purge",StringComparer.Ordinal))
            throw new UnauthorizedAccessException("此设备没有清除照片空间索引的权限，请使用具有清除权限的设备授权。");
        // Drain any previous frozen export before deleting its reference. This
        // also publishes the latest missing state required for photo purges.
        await PublishAsync(cancellationToken:cancellationToken,scope:scope).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease=await AcquirePublisherLeaseAsync(cancellationToken).ConfigureAwait(false);
            using var database=localDatabase.OpenIndependentConnection();
            string publisher,epoch,global;
            using(var query=database.Command("""
                SELECT p.publisher_id,p.epoch,e.global_id FROM sync_publishers p JOIN sync_entity_identities e ON e.publisher_id=p.publisher_id
                WHERE p.device_grant_id=$grant AND p.space_id=$space AND p.incarnation_id=$device AND p.state='publishing'
                    AND e.kind=$kind AND e.local_key=CAST($id AS TEXT)
                """))
            {
                query.Parameters.AddWithValue("$grant",session.GrantId);query.Parameters.AddWithValue("$space",session.SpaceId);
                query.Parameters.AddWithValue("$device",session.DeviceId);query.Parameters.AddWithValue("$kind",kind);query.Parameters.AddWithValue("$id",localId);
                using var row=query.ExecuteReader();
                if(!row.Read())
                {
                    using var exists=database.Command($"SELECT 1 FROM {(kind=="image"?"images":"videos")} WHERE id=$id");
                    exists.Parameters.AddWithValue("$id",localId);
                    if(exists.ExecuteScalar() is null) return true;
                    throw new InvalidDataException("published_media_identity_missing");
                }
                publisher=row.GetString(0);epoch=row.GetString(1);global=row.GetString(2);
            }
            using var resolved=await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/media-reference",
                new SpaceMediaReferenceRequest(publisher,epoch,scope,kind,global),DeviceAuthorization.Json,cancellationToken).ConfigureAwait(false);
            resolved.EnsureSuccessStatusCode();
            var target=await resolved.Content.ReadFromJsonAsync<SpaceMediaReference>(DeviceAuthorization.Json,cancellationToken).ConfigureAwait(false)
                ??throw new InvalidDataException("published_media_reference_missing");
            if(target.Id<=0 || !Guid.TryParseExact(target.MirrorEpoch,"N",out _)) throw new InvalidDataException("published_media_reference_invalid");
            using var request=new HttpRequestMessage(HttpMethod.Post,kind=="image"?$"api/gateway/v1/images/{target.Id}/purge-missing":$"api/gateway/v1/videos/{target.Id}/purge")
                { Content=JsonContent.Create(new { }) };
            request.Headers.Add("X-Picer-Mirror-Epoch",target.MirrorEpoch);
            using var response=await accountClient.SpaceClient.SendAsync(request,cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if((await response.Content.ReadFromJsonAsync<PipeFlagResponse>(DeviceAuthorization.Json,cancellationToken).ConfigureAwait(false))?.Value!=true)
                throw new InvalidDataException("published_media_purge_unconfirmed");
            if(kind=="image")
            {
                if(!new Picer.Core.Data.ImageRecordPurger(database).Purge(localId))
                    throw new InvalidOperationException("本机记录状态已变化，请刷新后检查清除结果。");
            }
            else new Picer.Core.Data.VideoRecordPurger(database).Purge(localId);
        }
        finally { _gate.Release(); }
        // Rebaseline removes the local reference and discards obsolete snapshots
        // using the service's confirmed surviving identity set.
        await PublishAsync(cancellationToken:cancellationToken,scope:scope).ConfigureAwait(false);
        return true;
    }
}
