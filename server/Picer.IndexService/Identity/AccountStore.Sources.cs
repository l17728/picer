using Picer.Core.Data;

namespace Picer.IndexService.Identity;

public sealed partial class AccountStore
{
    public void BindSourceAsOwner(string accountId, string spaceId, string agentGrantId, PicerDatabase database, long sourceId)
    {
        _authorizationFence.EnterReadLock();
        try
        {
            if (RequiresPasswordChange(accountId) || !ListSpaces(accountId).Any(item => item.Id == spaceId && item.Role == "owner"))
                throw new UnauthorizedAccessException("space_owner_required");
            if (agentGrantId.Length != 0 && !ListDevices(accountId).Any(item => item.Id == agentGrantId && item.SpaceId == spaceId && item.Kind == "agent" && item.State == "active"))
                throw new UnauthorizedAccessException("active_agent_required");
            using var transaction = database.BeginTransaction();
            using var update = database.Command("""
                UPDATE sync_source_bindings SET agent_grant_id=$agent WHERE source_id=$source
                AND publisher_id IN (SELECT publisher_id FROM sync_publishers WHERE space_id=$space)
                """);
            update.Parameters.AddWithValue("$space", spaceId);
            update.Parameters.AddWithValue("$source", sourceId);
            update.Parameters.AddWithValue("$agent", agentGrantId.Length == 0 ? DBNull.Value : agentGrantId);
            if (update.ExecuteNonQuery() != 1) throw new ArgumentException("source_not_in_space");
            transaction.Commit();
        }
        finally { _authorizationFence.ExitReadLock(); }
    }
}
