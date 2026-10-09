using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class AnalysisBillingMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE analysis_billing_events(
                request_id TEXT PRIMARY KEY, provider TEXT NOT NULL, model_version TEXT NOT NULL,
                input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL,
                estimated_cost_yuan REAL NOT NULL, recorded_utc TEXT NOT NULL);
            CREATE INDEX idx_analysis_billing_time ON analysis_billing_events(recorded_utc);
            CREATE TABLE analysis_budget_reservations(
                request_id TEXT PRIMARY KEY, amount_yuan REAL NOT NULL, created_utc TEXT NOT NULL,
                expires_utc TEXT NOT NULL);
            INSERT INTO analysis_billing_events
                SELECT lower(hex(randomblob(16))),provider,model_version,input_tokens,output_tokens,
                    estimated_cost_yuan,updated_utc FROM cloud_vision_analyses
                WHERE status='completed' AND estimated_cost_yuan>0;
            INSERT INTO analysis_billing_events
                SELECT lower(hex(randomblob(16))),provider,model_version,input_tokens,output_tokens,
                    estimated_cost_yuan,created_utc FROM video_summaries WHERE estimated_cost_yuan>0;
            CREATE INDEX IF NOT EXISTS idx_images_source_path_nocase ON images(source_id,path COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_videos_source_path_nocase ON videos(source_id,path COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_images_directory_path ON images(replace(path,char(92),'/') COLLATE NOCASE,status);
            CREATE INDEX IF NOT EXISTS idx_videos_directory_path ON videos(replace(path,char(92),'/') COLLATE NOCASE,status);
            ALTER TABLE detected_faces ADD COLUMN is_archived INTEGER NOT NULL DEFAULT 0;
            PRAGMA user_version=43;
            """;
        command.ExecuteNonQuery();
        FaceArchiveMigration.Apply(connection);
    }
}
