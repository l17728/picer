using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncReceiver
{
    public SpaceRebaselinePage Rebaseline(SpaceRebaselineRequest request)
    {
        if (!device.Allows("index.write") || !Guid.TryParseExact(request.LibraryId, "N", out _) || request.IncarnationId != device.DeviceId ||
            request.AfterKind.Length > 32 || request.AfterKey.Length > 32) throw new UnauthorizedAccessException();
        using var transaction = database.BeginTransaction();
        var epoch = SpaceEpoch();
        if (request.Epoch is not null && request.Epoch != epoch) throw new InvalidOperationException("space_epoch_reset_required");
        SpacePublisher publisher;
        string state;
        using (var query = Sql("SELECT publisher_id,last_sequence,state FROM sync_publishers WHERE library_id=$library AND incarnation_id=$incarnation AND device_grant_id=$grant AND space_id=$space",
            ("$library", request.LibraryId), ("$incarnation", request.IncarnationId), ("$grant", device.GrantId), ("$space", device.SpaceId)))
        using (var row = query.ExecuteReader())
        {
            if (!row.Read()) throw new UnauthorizedAccessException();
            publisher = new(row.GetString(0), request.LibraryId, request.IncarnationId, epoch, row.GetInt64(1),SpaceSyncProtocol.MetadataVersion);
            state = row.GetString(2);
        }
        if (request.Complete)
        {
            if (request.Epoch != epoch || state is not ("rebaseline" or "receiving") || state == "receiving" && publisher.LastSequence != 0)
                throw new InvalidOperationException("publisher_epoch_reset_required");
            Execute("UPDATE sync_publishers SET state='receiving',last_sequence=0 WHERE publisher_id=$id AND state='rebaseline'", ("$id", publisher.PublisherId));
            transaction.Commit(); return new(publisher with { LastSequence = 0 }, [], false, "", "");
        }
        if (state != "rebaseline") throw new InvalidOperationException("publisher_epoch_reset_required");
        var items = new List<SpaceEntityIdentity>(); var lastKey = request.AfterKey; var lastKind = request.AfterKind; var more = false;
        using (var query = Sql("SELECT kind,local_key,global_id FROM sync_entity_identities WHERE publisher_id=$id AND (kind,local_key)>($kind,$key) ORDER BY kind,local_key LIMIT 501",
            ("$id", publisher.PublisherId), ("$kind", request.AfterKind), ("$key", request.AfterKey)))
        using (var row = query.ExecuteReader())
        {
            while (row.Read())
            {
                if (items.Count == 500) { more = true; break; }
                lastKind = row.GetString(0); lastKey = row.GetString(1);
                items.Add(new(lastKind, long.Parse(lastKey, System.Globalization.CultureInfo.InvariantCulture), row.GetString(2)));
            }
        }
        transaction.Commit(); return new(publisher, items, more, lastKind, lastKey);
    }
}
