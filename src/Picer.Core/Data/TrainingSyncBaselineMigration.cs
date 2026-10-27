using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class TrainingSyncBaselineMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sync_training_baselines(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                kind TEXT NOT NULL CHECK(kind IN ('artifact','prototype','policy')),
                row_key TEXT NOT NULL,model_version TEXT NOT NULL,fingerprint TEXT NOT NULL,
                PRIMARY KEY(publisher_id,kind,row_key));
            UPDATE sync_publishers SET uploaded_metadata_revision=-1 WHERE state='publishing';
            PRAGMA user_version=91;
            """;
        command.ExecuteNonQuery(); transaction.Commit();
    }
}
