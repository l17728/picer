using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncReceiver
{
    public SpaceMediaReference ResolvePublishedMedia(SpaceMediaReferenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(!device.Allows("index.purge") || request.Kind is not ("image" or "video") || !Guid.TryParseExact(request.GlobalId,"N",out _))
            throw new UnauthorizedAccessException();
        using var transaction=database.BeginReadTransaction();
        ValidateAdoptionPublisher(request.PublisherId,request.Epoch,request.Scope);
        var table=request.Kind=="image"?"images":"videos";
        using var query=AdoptionSql($"""
            SELECT media.id,(SELECT epoch FROM mirror_revision WHERE id=1)
            FROM sync_entity_identities identity JOIN {table} media ON media.id=identity.target_id
            JOIN sync_source_bindings binding ON binding.publisher_id=identity.publisher_id AND binding.source_id=media.source_id
            WHERE identity.publisher_id=$publisher AND identity.kind=$kind AND identity.global_id=$global
                AND binding.local_source_id IN(SELECT value FROM json_each($sources))
                AND (($kind='image' AND $photos=1) OR ($kind='video' AND $videos=1))
            """,request.PublisherId,request.Scope);
        query.Transaction=transaction;query.Parameters.AddWithValue("$kind",request.Kind);query.Parameters.AddWithValue("$global",request.GlobalId);
        using var row=query.ExecuteReader();
        if(!row.Read()) throw new UnauthorizedAccessException("published_media_outside_scope");
        return new(row.GetInt64(0),row.GetString(1));
    }
}
