using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Picer.Core.Data;

internal static class ClusterModelPreferenceMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand(); command.Transaction=transaction;
        // Disposable recommendation cache: replaying a historical test fixture
        // may retain it; it is never authoritative user metadata.
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS semantic_cluster_model_recommendations(
                model_version TEXT PRIMARY KEY,recommended_threshold REAL NOT NULL,fingerprint TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        var modelQueries=new List<string>();
        foreach(var table in new[]{"clip_image_embeddings","semantic_content_clusters"})
        {
            command.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table";
            command.Parameters.AddWithValue("$table",table);var exists=command.ExecuteScalar() is not null;command.Parameters.Clear();
            if(exists) modelQueries.Add($"SELECT model_version FROM {table}");
        }
        var models=new List<string>();
        if(modelQueries.Count>0)
        {
            command.CommandText="SELECT DISTINCT model_version FROM ("+string.Join(" UNION ",modelQueries)+") WHERE length(trim(model_version))>0 LIMIT 2";
            using var rows=command.ExecuteReader();while(rows.Read()) models.Add(rows.GetString(0));
        }
        // Only an unambiguous historical model can inherit the legacy manual
        // threshold. Otherwise retain the legacy field without applying it to
        // arbitrary new models.
        if (models.Count==1 && models[0].Length<=256 && models[0].Trim()==models[0])
        {
            command.CommandText="SELECT automatic,manual_threshold FROM semantic_cluster_settings WHERE id=1";
            bool automatic=true; float threshold=0.82f;
            using (var row=command.ExecuteReader()) if(row.Read()) { automatic=row.GetBoolean(0);threshold=row.GetFloat(1); }
            if ((!automatic || threshold!=0.82f) && float.IsFinite(threshold) && threshold is >=0.70f and <=0.95f)
            {
                command.CommandText="""
                    INSERT OR IGNORE INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id)
                    VALUES('space-setting',1,$field,$value,$now,$op);
                    """;
                command.Parameters.AddWithValue("$field",PicerDatabase.ClusterPreferenceField(models[0]));
                command.Parameters.AddWithValue("$value",JsonSerializer.Serialize(new ClusterPreference(automatic,threshold) { ModelVersion=models[0] }));
                command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$op",Guid.NewGuid().ToString("N"));
                command.ExecuteNonQuery();command.Parameters.Clear();
            }
        }
        command.CommandText="PRAGMA user_version=68";command.ExecuteNonQuery();transaction.Commit();
    }
}
