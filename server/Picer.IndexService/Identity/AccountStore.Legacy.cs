namespace Picer.IndexService.Identity;

public sealed record LegacyMigrationState(string AccountId, string SpaceId, string State);

public sealed partial class AccountStore
{
    public LegacyMigrationState? GetLegacyMigration()
    {
        using var connection = Open();
        using var query = Command(connection, null, "SELECT account_id,space_id,state FROM legacy_migration WHERE id=1");
        using var row = query.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetString(1), row.GetString(2)) : null;
    }

    internal void PublishLegacySpace(LegacyMigrationState migration)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        if (Execute(connection, transaction, "UPDATE legacy_migration SET state='complete' WHERE id=1 AND account_id=$account AND space_id=$space AND state='preparing'",
                ("$account", migration.AccountId), ("$space", migration.SpaceId)) != 1)
            throw new InvalidOperationException("legacy_migration_state_changed");
        if (Execute(connection, transaction, "UPDATE spaces SET state='active' WHERE id=$space AND state='migrating'", ("$space", migration.SpaceId)) != 1)
            throw new InvalidOperationException("legacy_space_state_changed");
        transaction.Commit();
    }
}
