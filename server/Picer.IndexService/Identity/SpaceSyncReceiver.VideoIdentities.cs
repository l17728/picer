using System.Globalization;
using Picer.Core.Data;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncReceiver
{
    private static bool TryLocalVideoIdentity(string? identity, out string epoch, out long content)
    {
        epoch = ""; content = 0;
        var parts = identity?.Split(':');
        if (parts is not { Length: 3 } || parts[0] != "local" || !Guid.TryParseExact(parts[1], "N", out _) ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out content) || content <= 0) return false;
        epoch = parts[1]; return true;
    }

    private string? ImportVideoContentIdentity(string publisher, MetadataEdit edit, long target)
    {
        if (edit.Kind is not ("video-tag-link" or "video-concept") || edit.VideoContentIdentity is not { } identity ||
            identity.StartsWith("sha256:", StringComparison.Ordinal)) return edit.VideoContentIdentity;
        if (!TryLocalVideoIdentity(identity, out _, out _)) throw new ArgumentException("invalid_video_content_identity");
        // A database-local ID is meaningful only through this publisher's uploaded
        // content mapping. Never bind a delayed operation to the current path/ID.
        var content = Scalar("""
            SELECT m.content_id FROM sync_video_content_identities m
            WHERE m.publisher_id=$publisher AND m.source_identity=$identity AND EXISTS(
                SELECT 1 FROM videos v JOIN sync_video_owners candidate ON candidate.video_id=v.id
                JOIN sync_video_owners origin ON origin.video_id=$target AND candidate.owner_key=origin.owner_key
                WHERE v.content_id=m.content_id)
            """, ("$publisher", publisher), ("$identity", identity), ("$target", target));
        if (content is not long id) throw new ArgumentException("video_content_identity_unpublished");
        return "local:" + Scalar("SELECT epoch FROM mirror_revision WHERE id=1") + ":" + id.ToString(CultureInfo.InvariantCulture);
    }

    private MetadataEdit? ExportVideoContentIdentity(string publisher, MetadataEdit edit)
    {
        if(IsPhotoEdit(edit)) return ExportPhotoContentIdentity(publisher,edit);
        if (edit.Kind is not ("video-tag-link" or "video-concept") || edit.VideoContentIdentity is not { } identity ||
            identity.StartsWith("sha256:", StringComparison.Ordinal)) return edit;
        if (!TryLocalVideoIdentity(identity, out var epoch, out var content) ||
            epoch != Scalar("SELECT epoch FROM mirror_revision WHERE id=1") as string) return null;
        var sourceIdentity = Scalar("SELECT source_identity FROM sync_video_content_identities WHERE publisher_id=$publisher AND content_id=$content ORDER BY source_identity LIMIT 1",
            ("$publisher", publisher), ("$content", content)) as string;
        return sourceIdentity is null ? null : edit with { VideoContentIdentity = sourceIdentity };
    }
}
