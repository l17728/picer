using System.Text;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.Core.Search;

/// <summary>Versioned FTS5 document store for descriptions, labels and derived media text.</summary>
public sealed class MediaTextIndexRepository
{
    private readonly PicerDatabase _database;
    public MediaTextIndexRepository(PicerDatabase database)
    {
        _database = database;
        EnsureSchema();
    }

    public void UpsertImage(ImageRecord image, IEnumerable<string?> fields, string sourceVersion = "v1")
    {
        var content = string.Join(" ", fields.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{image.Id}|{image.FileSize}|{image.ModifiedUtc:o}|{sourceVersion}|{content}")));
        using var tx = _database.BeginTransaction();
        using (var doc = _database.Command("""
            INSERT INTO media_text_documents(media_id,media_type,content,fingerprint,source_version,updated_utc)
            VALUES($id,'photo',$content,$fingerprint,$version,$now)
            ON CONFLICT(media_id) DO UPDATE SET media_type=excluded.media_type,content=excluded.content,
              fingerprint=excluded.fingerprint,source_version=excluded.source_version,updated_utc=excluded.updated_utc
            """))
        {
            doc.Transaction = tx; doc.Parameters.AddWithValue("$id", image.Id); doc.Parameters.AddWithValue("$content", content);
            doc.Parameters.AddWithValue("$fingerprint", fingerprint); doc.Parameters.AddWithValue("$version", sourceVersion);
            doc.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o")); doc.ExecuteNonQuery();
        }
        using (var fts = _database.Command("DELETE FROM media_text_fts WHERE rowid=$id; INSERT INTO media_text_fts(rowid,content) VALUES($id,$content);"))
        {
            fts.Transaction = tx; fts.Parameters.AddWithValue("$id", image.Id); fts.Parameters.AddWithValue("$content", content); fts.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void UpsertVideo(VideoRecord video, IEnumerable<string?> fields, string sourceVersion = "v1")
    {
        var content = string.Join(" ", fields.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        var key = -video.Id; // image ids are positive; negative keys avoid cross-media collisions.
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            $"video:{video.Id}|{video.FileSize}|{video.ModifiedUtc:o}|{sourceVersion}|{content}")));
        using var tx = _database.BeginTransaction();
        using (var doc = _database.Command("""
            INSERT INTO media_text_documents(media_id,media_type,content,fingerprint,source_version,updated_utc)
            VALUES($id,'video',$content,$fingerprint,$version,$now)
            ON CONFLICT(media_id) DO UPDATE SET media_type=excluded.media_type,content=excluded.content,
              fingerprint=excluded.fingerprint,source_version=excluded.source_version,updated_utc=excluded.updated_utc
            """))
        {
            doc.Transaction = tx; doc.Parameters.AddWithValue("$id", key); doc.Parameters.AddWithValue("$content", content);
            doc.Parameters.AddWithValue("$fingerprint", fingerprint); doc.Parameters.AddWithValue("$version", sourceVersion);
            doc.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o")); doc.ExecuteNonQuery();
        }
        using (var fts = _database.Command("DELETE FROM media_text_fts WHERE rowid=$id; INSERT INTO media_text_fts(rowid,content) VALUES($id,$content);"))
        { fts.Transaction = tx; fts.Parameters.AddWithValue("$id", key); fts.Parameters.AddWithValue("$content", content); fts.ExecuteNonQuery(); }
        tx.Commit();
    }

    /// <summary>Rebuilds documents for existing completed cloud analyses without re-calling a model.</summary>
    public int RebuildFromCloudAnalyses(CancellationToken cancellationToken = default)
    {
        var rows = new List<(ImageRecord Image, string Content, string Version)>();
        using (var command = _database.Command("""
            SELECT i.*, c.summary, c.result_json, c.provider, c.model_version, c.prompt_version
            FROM images i JOIN cloud_vision_analyses c ON c.image_id=i.id
            WHERE c.status='completed' AND c.result_json IS NOT NULL
              AND c.updated_utc=(SELECT MAX(c2.updated_utc) FROM cloud_vision_analyses c2 WHERE c2.image_id=c.image_id AND c2.status='completed')
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = ReadImage(reader);
                var summary = reader.IsDBNull(reader.GetOrdinal("summary")) ? null : reader.GetString(reader.GetOrdinal("summary"));
                var json = reader.GetString(reader.GetOrdinal("result_json"));
                var version = $"cloud:{reader.GetString(reader.GetOrdinal("provider"))}:{reader.GetString(reader.GetOrdinal("model_version"))}:{reader.GetString(reader.GetOrdinal("prompt_version"))}";
                var fields = new List<string?> { image.Path, image.Camera, image.Gps, image.TakenUtc?.ToString("O"), summary };
                try
                {
                    var result = System.Text.Json.JsonSerializer.Deserialize<Picer.Core.Vision.CloudVisionResult>(json,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (result is not null) fields.AddRange((result.Scenes ?? []).Concat(result.Activities ?? []).Concat(result.Objects ?? []).Concat(result.SearchTerms ?? []));
                }
                catch (System.Text.Json.JsonException) { }
                rows.Add((image, string.Join(" ", fields.Where(v => !string.IsNullOrWhiteSpace(v))), version));
            }
        }
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpsertImage(row.Image, [row.Content], row.Version);
        }
        return rows.Count;
    }

    private static ImageRecord ReadImage(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")), SourceId = r.GetInt64(r.GetOrdinal("source_id")),
        Path = r.GetString(r.GetOrdinal("path")), FileSize = r.GetInt64(r.GetOrdinal("file_size")),
        ModifiedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("modified_utc")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        Sha256 = r.IsDBNull(r.GetOrdinal("sha256")) ? null : r.GetString(r.GetOrdinal("sha256")),
        Width = r.GetInt32(r.GetOrdinal("width")), Height = r.GetInt32(r.GetOrdinal("height")),
        TakenUtc = r.IsDBNull(r.GetOrdinal("taken_utc")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("taken_utc")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        Camera = r.IsDBNull(r.GetOrdinal("camera")) ? null : r.GetString(r.GetOrdinal("camera")),
        Gps = r.IsDBNull(r.GetOrdinal("gps")) ? null : r.GetString(r.GetOrdinal("gps")),
        Status = r.GetString(r.GetOrdinal("status")) == "active" ? ImageStatus.Active : ImageStatus.Missing,
        IndexedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("indexed_utc")), null, System.Globalization.DateTimeStyles.RoundtripKind)
    };

    private void EnsureSchema()
    {
        using var command = _database.Command("""
            CREATE TABLE IF NOT EXISTS media_text_documents(
              media_id INTEGER PRIMARY KEY, media_type TEXT NOT NULL, content TEXT NOT NULL DEFAULT '',
              fingerprint TEXT NOT NULL, source_version TEXT NOT NULL, updated_utc TEXT NOT NULL);
            CREATE VIRTUAL TABLE IF NOT EXISTS media_text_fts USING fts5(content);
            """);
        command.ExecuteNonQuery();
    }
}
