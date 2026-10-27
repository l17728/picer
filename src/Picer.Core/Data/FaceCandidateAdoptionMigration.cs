using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class FaceCandidateAdoptionMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            command.CommandText = $"""
                CREATE TRIGGER IF NOT EXISTS space_sync_adoption_revision_face_candidate_members_{operation}
                AFTER {operation} ON face_candidate_members
                WHEN EXISTS(SELECT 1 FROM sync_publishers WHERE state='receiving')
                BEGIN UPDATE sync_identity_offer_revision SET revision=revision+1 WHERE id=1; END;
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText = "DELETE FROM sync_adoption_scan_checkpoints; PRAGMA user_version=90;";
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
