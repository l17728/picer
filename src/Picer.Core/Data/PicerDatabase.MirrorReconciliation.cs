namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    // Fixed internal table names only. Staging avoids firing persistent triggers
    // for unchanged rows; publication stays inside the caller's transaction.
    private static readonly string[] TrainingMirrorTables =
        ["face_training_artifacts", "face_person_prototypes", "face_person_policies", "face_model_settings"];

    private void PrepareTrainingMirrorStages()
    {
        foreach (var table in TrainingMirrorTables)
        {
            using var command = Command($"CREATE TEMP TABLE IF NOT EXISTS stage_{table} AS SELECT * FROM {table} WHERE 0; DELETE FROM stage_{table};");
            command.ExecuteNonQuery();
        }
    }

    private void PublishTrainingMirrorStages()
    {
        var schemas = new Dictionary<string, (string[] Columns, string[] Keys)>();
        foreach (var table in TrainingMirrorTables)
        {
            using var query = Command($"PRAGMA table_info({table})");
            using var reader = query.ExecuteReader();
            var columns = new List<string>(); var keys = new List<(int Order, string Name)>();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
                if (reader.GetInt32(5) > 0) keys.Add((reader.GetInt32(5), reader.GetString(1)));
            }
            if (keys.Count == 0) throw new InvalidOperationException("mirror_table_requires_primary_key");
            schemas.Add(table, (columns.ToArray(), keys.OrderBy(key => key.Order).Select(key => key.Name).ToArray()));
        }
        foreach (var table in new[] { "face_person_policies", "face_person_prototypes", "face_training_artifacts", "face_model_settings" })
        {
            using var duplicate = Command($"SELECT 1 FROM stage_{table} GROUP BY {string.Join(',', schemas[table].Keys)} HAVING COUNT(*)>1 LIMIT 1");
            if (duplicate.ExecuteScalar() is not null) throw new InvalidOperationException("duplicate_mirror_key");
            using var index = Command($"CREATE INDEX IF NOT EXISTS stage_{table}_keys ON stage_{table}({string.Join(',', schemas[table].Keys)})");
            index.ExecuteNonQuery();
            var match = string.Join(" AND ", schemas[table].Keys.Select(key => $"s.{key} IS {table}.{key}"));
            using var delete = Command($"DELETE FROM {table} WHERE NOT EXISTS(SELECT 1 FROM stage_{table} s WHERE {match})");
            delete.ExecuteNonQuery();
        }
        foreach (var table in TrainingMirrorTables)
        {
            var (columns, keys) = schemas[table];
            var values = columns.Except(keys).ToArray();
            var assignments = string.Join(",", values.Select(column => $"{column}=excluded.{column}"));
            var changed = string.Join(" OR ", values.Select(column => $"{table}.{column} IS NOT excluded.{column}"));
            using var merge = Command($"INSERT INTO {table}({string.Join(',', columns)}) SELECT {string.Join(',', columns)} FROM stage_{table} WHERE true ON CONFLICT({string.Join(',', keys)}) DO UPDATE SET {assignments} WHERE {changed}");
            merge.ExecuteNonQuery();
            using var clear = Command($"DELETE FROM stage_{table}");
            clear.ExecuteNonQuery();
        }
    }
}
