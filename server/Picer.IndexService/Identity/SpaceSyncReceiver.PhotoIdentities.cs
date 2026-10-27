using Picer.Core.Data;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncReceiver
{
    private static bool IsPhotoEdit(MetadataEdit edit)=>edit.Kind is "photo-classification-confirmation" or "photo-classification-policy";
    private static bool IsLocalPhotoIdentity(string? identity)
    {
        var parts=identity?.Split(':');
        return parts is { Length:3 } && parts[0]=="local" && Guid.TryParseExact(parts[1],"N",out _) && Guid.TryParseExact(parts[2],"N",out _);
    }

    private void RecordPhotoSourceIdentity(string publisher,string identity,long target,string? sourceHash)
    {
        if(!IsLocalPhotoIdentity(identity)) throw new ArgumentException("invalid_photo_content_identity");
        var current=database.GetPhotoClassificationContentIdentity(target);
        using(var query=Sql("SELECT reference_id,content_identity FROM sync_photo_content_identities WHERE publisher_id=$publisher AND source_identity=$identity",
            ("$publisher",publisher),("$identity",identity)))
        {
            using var row=query.ExecuteReader();
            if(row.Read() && (row.GetInt64(0)!=target || row.GetString(1)!=current && row.GetString(1)!=database.GetLocalPhotoContentIdentity(target)))
                throw new ArgumentException("photo_content_identity_rebound");
        }
        Execute("""
            INSERT INTO sync_photo_content_identities(publisher_id,source_identity,reference_id,content_identity,source_sha256)
            VALUES($publisher,$identity,$target,$content,$sha)
            ON CONFLICT(publisher_id,source_identity) DO UPDATE SET content_identity=excluded.content_identity,source_sha256=excluded.source_sha256
            """,("$publisher",publisher),("$identity",identity),("$target",target),("$content",current),
            ("$sha",sourceHash is { Length:64 } && sourceHash.All(Uri.IsHexDigit)?sourceHash.ToLowerInvariant():null));
    }

    private string? ImportPhotoContentIdentity(string publisher,MetadataEdit edit,long target)
    {
        if(!IsPhotoEdit(edit)) return edit.PhotoContentIdentity;
        var identity=edit.PhotoContentIdentity??throw new ArgumentException("photo_content_identity_required");
        if(identity.StartsWith("sha256:",StringComparison.Ordinal)) return identity.ToLowerInvariant();
        if(!IsLocalPhotoIdentity(identity)) throw new ArgumentException("invalid_photo_content_identity");
        var mapped=Scalar("SELECT content_identity FROM sync_photo_content_identities WHERE publisher_id=$publisher AND source_identity=$identity AND reference_id=$target",
            ("$publisher",publisher),("$identity",identity),("$target",target)) as string;
        if(mapped is null) throw new ArgumentException("photo_content_identity_unpublished");
        if(mapped.StartsWith("sha256:",StringComparison.Ordinal)) return mapped;
        if(mapped!=database.GetLocalPhotoContentIdentity(target)) throw new ArgumentException("photo_content_identity_changed");
        return database.GetPhotoClassificationContentIdentity(target);
    }

    private MetadataEdit? ExportPhotoContentIdentity(string publisher,MetadataEdit edit)
    {
        if(!IsPhotoEdit(edit) || edit.PhotoContentIdentity is not { } identity) return edit;
        var target=Scalar("SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='image' AND local_key=$local",
            ("$publisher",publisher),("$local",edit.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if(target is not long reference) return null;
        var local=database.GetLocalPhotoContentIdentity(reference);var current=database.GetPhotoClassificationContentIdentity(reference);
        using var query=Sql("""
            SELECT source_identity,source_sha256 FROM sync_photo_content_identities
            WHERE publisher_id=$publisher AND reference_id=$target
                AND (content_identity=$identity OR (content_identity=$local AND $identity=$current))
            ORDER BY rowid DESC LIMIT 1
            """,("$publisher",publisher),("$target",reference),("$identity",identity),("$local",local),("$current",current));
        using var row=query.ExecuteReader();
        if(!row.Read()) return identity.StartsWith("sha256:",StringComparison.Ordinal)?edit:null;
        return identity.StartsWith("sha256:",StringComparison.Ordinal) && !row.IsDBNull(1)
            ? edit : edit with { PhotoContentIdentity=row.GetString(0) };
    }
}
