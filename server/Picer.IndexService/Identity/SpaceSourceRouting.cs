using Picer.Core.Data;
using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

public static class SpaceSourceRouting
{
    public static int Bind(PicerDatabase database, AccountStore accounts, AuthorizedDevice device, SpaceSourceBindingRequest request)
    {
        if (!device.Allows("source.publish") || device.Kind != "gui") throw new UnauthorizedAccessException("source_owner_required");
        if (!Guid.TryParseExact(request.PublisherId, "N", out _) || !Guid.TryParseExact(request.AgentGrantId, "N", out _) ||
            request.LocalSourceIds is null || request.LocalSourceIds.Count is < 1 or > 1000 || request.LocalSourceIds.Any(id => id <= 0))
            throw new ArgumentException("invalid_source_binding");
        var agent = accounts.ListDevices(device.AccountId).SingleOrDefault(item => item.Id == request.AgentGrantId &&
            item.SpaceId == device.SpaceId && item.Kind == "agent" && item.State == "active");
        if (agent is null) throw new UnauthorizedAccessException("agent_approval_required");
        using var transaction = database.BeginTransaction();
        using (var publisher = database.Command("SELECT COUNT(*) FROM sync_publishers WHERE publisher_id=$id AND device_grant_id=$grant AND space_id=$space"))
        {
            publisher.Parameters.AddWithValue("$id", request.PublisherId); publisher.Parameters.AddWithValue("$grant", device.GrantId);
            publisher.Parameters.AddWithValue("$space", device.SpaceId);
            if (Convert.ToInt64(publisher.ExecuteScalar()) != 1) throw new UnauthorizedAccessException("publisher_owner_required");
        }
        var count = 0;
        foreach (var source in request.LocalSourceIds.Distinct())
        {
            using var update = database.Command("UPDATE sync_source_bindings SET agent_grant_id=$agent WHERE publisher_id=$publisher AND local_source_id=$source");
            update.Parameters.AddWithValue("$agent", agent.Id); update.Parameters.AddWithValue("$publisher", request.PublisherId);
            update.Parameters.AddWithValue("$source", source);
            if (update.ExecuteNonQuery() != 1) throw new ArgumentException("source_not_published");
            count++;
        }
        transaction.Commit(); return count;
    }

    public static string? Resolve(PicerDatabase database, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? selection = null;
        long id;
        if (parts.Length == 6 && parts[0] == "api" && parts[1] == "gateway" && parts[2] == "v1" && parts[3] == "assets" && long.TryParse(parts[5], out id))
            selection = parts[4] switch { "images" => "SELECT source_id FROM images WHERE id=$id", "videos" => "SELECT source_id FROM videos WHERE id=$id", _ => null };
        else if (parts.Length == 5 && parts[0] == "api" && parts[1] == "gateway" && parts[2] == "v1" && long.TryParse(parts[4], out id))
            selection = parts[3] switch
            {
                "thumbs" or "previews" => "SELECT source_id FROM images WHERE id=$id",
                "video-thumbs" => "SELECT source_id FROM videos WHERE id=$id",
                "face-thumbs" => "SELECT i.source_id FROM detected_faces f JOIN images i ON i.id=f.image_id WHERE f.id=$id",
                "video-keyframes" => "SELECT v.source_id FROM video_keyframes f JOIN videos v ON v.content_id=f.video_id JOIN sync_source_bindings b ON b.source_id=v.source_id WHERE f.id=$id AND b.agent_grant_id IS NOT NULL ORDER BY v.id LIMIT 1",
                _ => null
            };
        else return null;
        if (selection is null || id <= 0) return null;
        using var query = database.Command("SELECT agent_grant_id FROM sync_source_bindings WHERE source_id=(" + selection + ")");
        query.Parameters.AddWithValue("$id", id);
        return query.ExecuteScalar() as string;
    }

    public static string? ResolveUniqueAgent(PicerDatabase database)
    {
        using var query = database.Command("""
            SELECT DISTINCT agent_grant_id
            FROM sync_source_bindings
            WHERE agent_grant_id IS NOT NULL AND trim(agent_grant_id)<>''
            LIMIT 2
            """);
        using var reader = query.ExecuteReader();
        string? agent = null;
        while (reader.Read())
        {
            if (agent is not null) return null;
            agent = reader.GetString(0);
        }
        return agent;
    }
}
