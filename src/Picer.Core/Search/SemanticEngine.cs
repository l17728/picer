using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.Search;

/// <summary>
/// Semantic classification engine: tags, albums, auto-rules, and AI suggestions.
/// Purely logical — does not move files on disk.
/// </summary>
public static class SemanticEngine
{
    #region Tags

    public static List<Tag> GetTags(PicerDatabase db)
    {
        using var cmd = db.Command("SELECT * FROM tags ORDER BY name");
        var tags = new List<Tag>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            tags.Add(new Tag { Id = r.GetInt64(0), Name = r.GetString(1), CreatedUtc = Parse(r.GetString(2)) });
        return tags;
    }

    public static Tag AddTag(PicerDatabase db, string name)
    {
        using var cmd = db.Command("INSERT INTO tags (name, created_utc) VALUES ($n, $c); SELECT last_insert_rowid();");
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$c", Fmt(DateTime.UtcNow));
        var id = (long)cmd.ExecuteScalar()!;
        return new Tag { Id = id, Name = name.Trim(), CreatedUtc = DateTime.UtcNow };
    }

    public static void DeleteTag(PicerDatabase db, long tagId)
    {
        using (var tx = db.Command("DELETE FROM image_tags WHERE tag_id = $id"))
        {
            tx.Parameters.AddWithValue("$id", tagId);
            tx.ExecuteNonQuery();
        }
        using (var cmd = db.Command("DELETE FROM tags WHERE id = $id"))
        {
            cmd.Parameters.AddWithValue("$id", tagId);
            cmd.ExecuteNonQuery();
        }
    }

    public static void AssignTag(PicerDatabase db, long imageId, long tagId)
    {
        using var cmd = db.Command("INSERT OR IGNORE INTO image_tags (image_id, tag_id) VALUES ($img, $tag)");
        cmd.Parameters.AddWithValue("$img", imageId);
        cmd.Parameters.AddWithValue("$tag", tagId);
        cmd.ExecuteNonQuery();
    }

    public static void RemoveTag(PicerDatabase db, long imageId, long tagId)
    {
        using var cmd = db.Command("DELETE FROM image_tags WHERE image_id = $img AND tag_id = $tag");
        cmd.Parameters.AddWithValue("$img", imageId);
        cmd.Parameters.AddWithValue("$tag", tagId);
        cmd.ExecuteNonQuery();
    }

    public static List<Tag> GetImageTags(PicerDatabase db, long imageId)
    {
        using var cmd = db.Command("SELECT t.* FROM tags t INNER JOIN image_tags it ON it.tag_id = t.id WHERE it.image_id = $img ORDER BY t.name");
        cmd.Parameters.AddWithValue("$img", imageId);
        var tags = new List<Tag>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            tags.Add(new Tag { Id = r.GetInt64(0), Name = r.GetString(1), CreatedUtc = Parse(r.GetString(2)) });
        return tags;
    }

    #endregion

    #region Albums

    public static List<Album> GetAlbums(PicerDatabase db)
    {
        using var cmd = db.Command("SELECT * FROM albums ORDER BY created_utc DESC");
        var albums = new List<Album>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            albums.Add(new Album
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                CreatorId = r.IsDBNull(2) ? (long?)null : r.GetInt64(2),
                CreatedUtc = Parse(r.GetString(3)),
                Description = r.IsDBNull(4) ? null : r.GetString(4)
            });
        return albums;
    }

    public static Album AddAlbum(PicerDatabase db, string name, string? description = null, long? creatorId = null)
    {
        using var cmd = db.Command("INSERT INTO albums (name, creator_id, created_utc, description) VALUES ($n, $cid, $c, $d); SELECT last_insert_rowid();");
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$cid", (object?)creatorId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", Fmt(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("$d", (object?)description ?? DBNull.Value);
        var id = (long)cmd.ExecuteScalar()!;
        return new Album { Id = id, Name = name.Trim(), CreatedUtc = DateTime.UtcNow, Description = description };
    }

    public static void DeleteAlbum(PicerDatabase db, long albumId)
    {
        using (var cmd = db.Command("DELETE FROM album_images WHERE album_id = $id"))
        {
            cmd.Parameters.AddWithValue("$id", albumId);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = db.Command("DELETE FROM albums WHERE id = $id"))
        {
            cmd.Parameters.AddWithValue("$id", albumId);
            cmd.ExecuteNonQuery();
        }
    }

    public static void AddToAlbum(PicerDatabase db, long albumId, long imageId)
    {
        using var cmd = db.Command("INSERT OR IGNORE INTO album_images (album_id, image_id, added_utc) VALUES ($aid, $iid, $ts)");
        cmd.Parameters.AddWithValue("$aid", albumId);
        cmd.Parameters.AddWithValue("$iid", imageId);
        cmd.Parameters.AddWithValue("$ts", Fmt(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }

    public static void RemoveFromAlbum(PicerDatabase db, long albumId, long imageId)
    {
        using var cmd = db.Command("DELETE FROM album_images WHERE album_id = $aid AND image_id = $iid");
        cmd.Parameters.AddWithValue("$aid", albumId);
        cmd.Parameters.AddWithValue("$iid", imageId);
        cmd.ExecuteNonQuery();
    }

    public static List<ImageRecord> GetAlbumImages(PicerDatabase db, long albumId)
    {
        using var cmd = db.Command("SELECT i.* FROM images i INNER JOIN album_images ai ON ai.image_id = i.id WHERE ai.album_id = $aid ORDER BY i.taken_utc IS NULL, i.taken_utc DESC, i.path LIMIT 1000");
        cmd.Parameters.AddWithValue("$aid", albumId);
        return ReadRecords(cmd.ExecuteReader());
    }

    public static int GetAlbumImageCount(PicerDatabase db, long albumId)
    {
        using var cmd = db.Command("SELECT COUNT(*) FROM album_images WHERE album_id = $aid");
        cmd.Parameters.AddWithValue("$aid", albumId);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    #endregion

    #region Auto Classification

    /// <summary>
    /// Automatically creates time-line albums for each year/month combination that has images with taken dates.
    /// Returns list of created albums.
    /// </summary>
    public static List<Album> AutoCreateTimeLineAlbums(PicerDatabase db)
    {
        // First get all distinct year-month combinations from images with taken_utc
        using var cmd = db.Command(@"
            SELECT DISTINCT 
                strftime('%Y', taken_utc) as year,
                strftime('%m', taken_utc) as month
            FROM images 
            WHERE taken_utc IS NOT NULL
            ORDER BY year, month
        ");

        var albums = new List<Album>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var year = reader.GetString(0);
            var month = reader.GetString(1);
            var albumName = $"{year}年{month}月照片";

            // Check if album already exists
            var checkCmd = db.Command("SELECT id FROM albums WHERE name = $name");
            checkCmd.Parameters.AddWithValue("$name", albumName);
            var existingId = checkCmd.ExecuteScalar() as long?;

            if (existingId.HasValue && existingId.Value > 0)
            {
                // Update existing album's description
                var updateCmd = db.Command("UPDATE albums SET description = $desc WHERE id = $id");
                updateCmd.Parameters.AddWithValue("$desc", $"时间线相册（自动生成）");
                updateCmd.Parameters.AddWithValue("$id", existingId.Value);
                updateCmd.ExecuteNonQuery();
                continue;
            }

            // Create new album
            var album = AddAlbum(db, albumName, "时间线相册（自动生成）", null);
            albums.Add(album);

            // Add all images for this month to the album
            var addCmd = db.Command(@"
                INSERT OR IGNORE INTO album_images (album_id, image_id, added_utc)
                SELECT $aid, i.id, $ts FROM images i
                WHERE STRFTIME('%Y', i.taken_utc) = $year AND STRFTIME('%m', i.taken_utc) = $month
            ");
            addCmd.Parameters.AddWithValue("$aid", album.Id);
            addCmd.Parameters.AddWithValue("$ts", Fmt(DateTime.UtcNow));
            addCmd.Parameters.AddWithValue("$year", year);
            addCmd.Parameters.AddWithValue("$month", month);
            addCmd.ExecuteNonQuery();
        }

        return albums;
    }

    /// <summary>
    /// Automatically creates camera-specific albums for all unique camera brands.
    /// Also adds a "CameraBrand" tag to matching images.
    /// </summary>
    public static List<Album> AutoCreateCameraAlbums(PicerDatabase db, bool autoTag = true)
    {
        using var cmd = db.Command(@"
            SELECT DISTINCT camera FROM images 
            WHERE camera IS NOT NULL AND camera <> ''
            ORDER BY camera
        ");

        var albums = new List<Album>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var cameraName = reader.GetString(0).Split(' ')[0]; // Use brand only
            var albumName = $"相机: {cameraName}";

            // Check if album already exists
            var checkCmd = db.Command("SELECT id FROM albums WHERE name = $name");
            checkCmd.Parameters.AddWithValue("$name", albumName);
            var existingId = checkCmd.ExecuteScalar() as long?;

            if (existingId.HasValue && existingId.Value > 0)
                continue; // Skip existing

            // Create album
            var album = AddAlbum(db, albumName, "按相机品牌自动创建", null);
            albums.Add(album);

            // Add matching images
            var addCmd = db.Command(@"
                INSERT OR IGNORE INTO album_images (album_id, image_id, added_utc)
                SELECT $aid, i.id, $ts FROM images i
                WHERE i.camera LIKE $pattern
            ");
            addCmd.Parameters.AddWithValue("$aid", album.Id);
            addCmd.Parameters.AddWithValue("$ts", Fmt(DateTime.UtcNow));
            addCmd.Parameters.AddWithValue("$pattern", cameraName + "%");
            addCmd.ExecuteNonQuery();

            // Auto-tag if enabled
            if (autoTag)
            {
                EnsureAndAssignTag(db, cameraName.ToLowerInvariant().Replace(" ", "_"), album.Id);
            }
        }

        return albums;
    }

    private static void EnsureAndAssignTag(PicerDatabase db, string tagName, long albumId)
    {
        using var cmd = db.Command("SELECT id FROM tags WHERE name = $name");
        cmd.Parameters.AddWithValue("$name", tagName);
        var tagId = cmd.ExecuteScalar() as long?;

        if (!tagId.HasValue || tagId.Value <= 0)
            tagId = AddTag(db, tagName).Id;

        // Add all album images to tag
        using var imgCmd = db.Command("SELECT image_id FROM album_images WHERE album_id = $aid");
        imgCmd.Parameters.AddWithValue("$aid", albumId);
        using var imgReader = imgCmd.ExecuteReader();
        while (imgReader.Read())
            AssignTag(db, imgReader.GetInt64(0), tagId.Value);
    }

    #endregion

    #region Local metadata suggestions

    /// <summary>
    /// Suggests tags based on GPS coordinates or EXIF data.
    /// Suggestions are deterministic local rules and are kept separate from user tags.
    /// </summary>
    public static List<SuggestedTag> ExtractAISuggestions(PicerDatabase db, long imageId)
    {
        var suggestions = new List<SuggestedTag>();

        using var cmd = db.Command(@"
            SELECT gps, width, height, taken_utc, camera FROM images WHERE id = $id
        ");
        cmd.Parameters.AddWithValue("$id", imageId);

        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                if (!reader.IsDBNull(0))
                    suggestions.Add(new SuggestedTag("有GPS信息", Confidence.Medium));

                if (!reader.IsDBNull(1))
                {
                    var w = reader.GetInt32(1);
                    var h = reader.GetInt32(2);
                    if (w > 3000 || h > 3000)
                        suggestions.Add(new SuggestedTag("高像素", Confidence.High));
                    if (w > h)
                        suggestions.Add(new SuggestedTag("横构图", Confidence.Medium));
                }

                if (!reader.IsDBNull(3))
                {
                    var taken = Parse(reader.GetString(3));
                    if (taken.Hour >= 6 && taken.Hour < 9)
                        suggestions.Add(new SuggestedTag("清晨拍摄", Confidence.Low));
                    else if (taken.Hour >= 17 && taken.Hour < 20)
                        suggestions.Add(new SuggestedTag("黄昏拍摄", Confidence.Low));
                }
            }
        }

        using var rejected = db.Command("SELECT tag_name FROM rejected_suggestions WHERE image_id = $id");
        rejected.Parameters.AddWithValue("$id", imageId);
        var rejectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var result = rejected.ExecuteReader()) while (result.Read()) rejectedNames.Add(result.GetString(0));
        return suggestions.Where(suggestion => !rejectedNames.Contains(suggestion.TagName)).ToList();
    }

    public static void RejectSuggestion(PicerDatabase db, long imageId, string tagName)
    {
        using var cmd = db.Command("INSERT OR REPLACE INTO rejected_suggestions(image_id, tag_name, rejected_utc) VALUES($id, $tag, $utc)");
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$tag", tagName);
        cmd.Parameters.AddWithValue("$utc", Fmt(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }

    #endregion

    private static string Fmt(DateTime dt) => dt.ToUniversalTime().ToString("o");

    private static DateTime Parse(string s) =>
        DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static List<ImageRecord> ReadRecords(SqliteDataReader reader)
    {
        var list = new List<ImageRecord>();
        while (reader.Read())
        {
            var ordSha = reader.GetOrdinal("sha256");
            var ordThumb = reader.GetOrdinal("thumb_path");
            var ordCam = reader.GetOrdinal("camera");
            var ordGps = reader.GetOrdinal("gps");
            var ordTaken = reader.GetOrdinal("taken_utc");
            var ordModified = reader.GetOrdinal("modified_utc");
            var ordIndexed = reader.GetOrdinal("indexed_utc");
            var ordAhash = reader.GetOrdinal("ahash");
            var ordDhash = reader.GetOrdinal("dhash");

            list.Add(new ImageRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                SourceId = reader.GetInt64(reader.GetOrdinal("source_id")),
                Path = reader.GetString(reader.GetOrdinal("path")),
                FileSize = reader.GetInt64(reader.GetOrdinal("file_size")),
                ModifiedUtc = Parse(reader.GetString(ordModified)),
                Sha256 = reader.IsDBNull(ordSha) ? null : reader.GetString(ordSha),
                Width = reader.GetInt32(reader.GetOrdinal("width")),
                Height = reader.GetInt32(reader.GetOrdinal("height")),
                TakenUtc = reader.IsDBNull(ordTaken) ? null : Parse(reader.GetString(ordTaken)),
                Camera = reader.IsDBNull(ordCam) ? null : reader.GetString(ordCam),
                Gps = reader.IsDBNull(ordGps) ? null : reader.GetString(ordGps),
                ThumbPath = reader.IsDBNull(ordThumb) ? null : reader.GetString(ordThumb),
                Status = reader.GetString(reader.GetOrdinal("status")) == "active" ? ImageStatus.Active : ImageStatus.Missing,
                IndexedUtc = Parse(reader.GetString(ordIndexed)),
                AverageHash = reader.GetInt64(ordAhash),
                DifferenceHash = reader.GetInt64(ordDhash)
            });
        }
        return list;
    }
}
