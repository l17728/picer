using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace Picer.Core.Data;

internal static class FaceArchiveMigration
{
    public static void Apply(SqliteConnection connection)
    {
        var triggers = new List<(string Name, string Sql)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='trigger' AND name IN ('content_capture_detected_faces_insert','content_capture_detected_faces_update','content_restore_image','content_restore_image_update')";
            using var reader = read.ExecuteReader();
            while (reader.Read()) triggers.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var (name, original) in triggers)
        {
            var sql = original;
            if (name.StartsWith("content_capture_", StringComparison.Ordinal) && !sql.Contains("'is_archived'", StringComparison.Ordinal))
                sql = sql.Replace("json_object(", "json_object('is_archived',r.is_archived,", StringComparison.Ordinal);
            if (name.StartsWith("content_restore_", StringComparison.Ordinal))
                sql = Regex.Replace(sql, @"INSERT OR IGNORE INTO detected_faces\(([^)]+)\) SELECT ", match =>
                    match.Groups[1].Value.Split(',').Contains("is_archived") ? match.Value :
                    "INSERT OR IGNORE INTO detected_faces(is_archived," + match.Groups[1].Value +
                    ") SELECT COALESCE(json_extract(d.payload,'$.is_archived'),0),", RegexOptions.CultureInvariant);
            if (sql == original) continue;
            using var replace = connection.CreateCommand();
            // Names are from the fixed IN-list above, never caller-controlled SQL identifiers.
            replace.CommandText = "DROP TRIGGER " + name + ";" + sql;
            replace.ExecuteNonQuery();
        }
        using var seed = connection.CreateCommand();
        seed.CommandText = "UPDATE content_derivatives SET payload=json_set(payload,'$.is_archived',0) WHERE kind='detected_faces' AND json_extract(payload,'$.is_archived') IS NULL";
        seed.ExecuteNonQuery();
    }
}
