using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Diagnostics;
using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.Core.Search;

public sealed record SearchCursor(object SortValue, long ImageId);
public sealed record SearchPage(IReadOnlyList<ImageRecord> Items, SearchCursor? NextCursor, int TotalCount);

public static class IndexSearchEngine
{
    public static async Task<SearchPage> SearchPagedAsync(
        PicerDatabase db,
        SearchFilters filters,
        int pageSize = 200,
        SearchCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var itemsTask = Task.Run(() => SearchPaged(db.OpenReadConnection(), filters, pageSize, cursor,
            cancellationToken, disposeConnection: true, includeCount: false), cancellationToken);
        var countTask = Task.Run(() => CountMatches(db, filters, cancellationToken), cancellationToken);
        await Task.WhenAll(itemsTask, countTask).ConfigureAwait(false);
        var itemsPage = await itemsTask.ConfigureAwait(false);
        return itemsPage with { TotalCount = await countTask.ConfigureAwait(false) };
    }

    public static SearchPage SearchPaged(PicerDatabase db, SearchFilters filters, int pageSize = 200, SearchCursor? cursor = null)
        => SearchPaged(db.OpenReadConnection(), filters, pageSize, cursor, CancellationToken.None, disposeConnection: true);

    private static SearchPage SearchPaged(
        SqliteConnection connection,
        SearchFilters filters,
        int pageSize,
        SearchCursor? cursor,
        CancellationToken cancellationToken,
        bool disposeConnection = false,
        bool includeCount = true)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var conditions = new List<string>();
            var parameters = new List<(string Name, object Value)>();
            AddFilterConditions(filters, conditions, parameters);

        var orderCol = SortColumn(filters.SortBy);
        var direction = filters.SortDir == SortDirection.Ascending ? "ASC" : "DESC";
        if (cursor is not null)
        {
            var op = filters.SortDir == SortDirection.Ascending ? ">" : "<";
            conditions.Add($"({orderCol} {op} $cursorValue OR ({orderCol} = $cursorValue AND t.id {op} $cursorId))");
            parameters.Add(("cursorValue", cursor.SortValue));
            parameters.Add(("cursorId", cursor.ImageId));
        }

        var where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
        var sql = $"""
            SELECT t.id, t.source_id, t.path, t.file_size, t.modified_utc, t.sha256,
                   t.width, t.height, t.taken_utc, t.camera, t.gps, t.thumb_path, t.status, t.indexed_utc, t.file_created_utc
            FROM {FromClause(filters)} {where}
            ORDER BY {orderCol} {direction}, t.id {direction}
            LIMIT $pageSize
            """;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.Parameters.AddWithValue("pageSize", pageSize);
        using var registration = cancellationToken.Register(cmd.Cancel);
        var items = ReadRecords(cmd.ExecuteReader());
        cancellationToken.ThrowIfCancellationRequested();
        var next = items.Count == pageSize ? CreateCursor(items[^1], filters.SortBy) : null;
        var page = new SearchPage(items, next, includeCount ? CountMatches(connection, filters, cancellationToken) : -1);
        PicerEventSource.Log.SearchCompleted(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, page.Items.Count);
        return page;
        }
        finally
        {
            if (disposeConnection) connection.Dispose();
        }
    }

    /// <summary>
    /// Full-text search backed by SQLite FTS5, plus structured filters.
    /// </summary>
    public static List<ImageRecord> Search(PicerDatabase db, SearchFilters filters)
    {
        var conditions = new List<string>();
        var parameters = new List<(string Name, object Value)>();

        // Text search on path filename, camera, gps
        if (!string.IsNullOrWhiteSpace(filters.Query))
        {
            AddTextCondition(filters.Query, conditions, parameters);
        }
        AddGeoConditions(filters, conditions, parameters);

        // Source filter
        if (filters.SourceId.HasValue && filters.SourceId.Value > 0)
        {
            conditions.Add("t.source_id = $sourceId");
            parameters.Add(("sourceId", filters.SourceId.Value));
        }

        // Status
        if (!filters.IncludeMissing)
            conditions.Add("t.status = 'active'");

        // Date range
        if (filters.TakenAfter.HasValue)
        {
            conditions.Add("t.taken_utc >= $takenAfter");
            parameters.Add(("takenAfter", Fmt(filters.TakenAfter.Value)));
        }
        if (filters.TakenBefore.HasValue)
        {
            conditions.Add("t.taken_utc <= $takenBefore");
            parameters.Add(("takenBefore", Fmt(filters.TakenBefore.Value)));
        }

        // Dimensions
        AddIfInt(conditions, parameters, "t.width >= $minW", filters.MinWidth, "minW");
        AddIfInt(conditions, parameters, "t.width <= $maxW", filters.MaxWidth, "maxW");
        AddIfInt(conditions, parameters, "t.height >= $minH", filters.MinHeight, "minH");
        AddIfInt(conditions, parameters, "t.height <= $maxH", filters.MaxHeight, "maxH");

        // File size
        AddIfLong(conditions, parameters, "t.file_size >= $minFS", filters.MinFileSize, "minFS");
        AddIfLong(conditions, parameters, "t.file_size <= $maxFS", filters.MaxFileSize, "maxFS");

        // Camera brand
        if (!string.IsNullOrEmpty(filters.CameraBrand))
        {
            conditions.Add("LOWER(t.camera) LIKE $cam");
            parameters.Add(("cam", "%" + filters.CameraBrand.ToLowerInvariant() + "%"));
        }
        if (filters.TagId is > 0)
        {
            conditions.Add("EXISTS (SELECT 1 FROM image_tags it WHERE it.image_id = t.id AND it.tag_id = $tagId)");
            parameters.Add(("tagId", filters.TagId.Value));
        }
        if (filters.PersonId is > 0)
        {
            conditions.Add("EXISTS (SELECT 1 FROM detected_faces pf JOIN face_person_assignments pa ON pa.face_id=pf.id WHERE pf.image_id=t.id AND pa.person_id=$personId AND pa.state='confirmed')");
            parameters.Add(("personId", filters.PersonId.Value));
        }
        AddVisualConceptConditions(filters, conditions, parameters);

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        var orderCol = SortColumn(filters.SortBy);
        var direction = filters.SortDir == SortDirection.Ascending ? "ASC" : "DESC";

        var sql = $"""
            SELECT t.id, t.source_id, t.path, t.file_size, t.modified_utc, t.sha256,
                   t.width, t.height, t.taken_utc, t.camera, t.gps, t.thumb_path, t.status, t.indexed_utc, t.file_created_utc
            FROM {FromClause(filters)}
            {whereClause}
            ORDER BY {orderCol} {direction}
            LIMIT 5000
            """;

        using var cmd = db.Command(sql);
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);

        return ReadRecords(cmd.ExecuteReader());
    }

    public static List<string> GetDistinctValues(PicerDatabase db, string column)
    {
        var sql = $"SELECT DISTINCT {column} FROM images WHERE {column} IS NOT NULL AND {column} <> '' ORDER BY {column}";
        using var cmd = db.Command(sql);
        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var v = reader.GetString(0);
            if (!string.IsNullOrEmpty(v))
                results.Add(v);
        }
        return results;
    }

    public static int CountMatches(PicerDatabase db, SearchFilters filters)
    {
        using var connection = db.OpenReadConnection();
        return CountMatches(connection, filters, CancellationToken.None);
    }

    private static int CountMatches(PicerDatabase db, SearchFilters filters, CancellationToken cancellationToken)
    {
        using var connection = db.OpenReadConnection();
        return CountMatches(connection, filters, cancellationToken);
    }

    private static int CountMatches(SqliteConnection connection, SearchFilters filters, CancellationToken cancellationToken)
    {
        var conditions = new List<string>();
        var parameters = new List<(string Name, object Value)>();

        if (!string.IsNullOrWhiteSpace(filters.Query))
        {
            AddTextCondition(filters.Query, conditions, parameters);
        }
        AddGeoConditions(filters, conditions, parameters);
        if (filters.SourceId.HasValue && filters.SourceId.Value > 0)
        {
            conditions.Add("t.source_id = $sourceId");
            parameters.Add(("sourceId", filters.SourceId.Value));
        }
        if (!filters.IncludeMissing)
            conditions.Add("t.status = 'active'");
        if (filters.TakenAfter.HasValue)
        {
            conditions.Add("t.taken_utc >= $takenAfter");
            parameters.Add(("takenAfter", Fmt(filters.TakenAfter.Value)));
        }
        if (filters.TakenBefore.HasValue)
        {
            conditions.Add("t.taken_utc <= $takenBefore");
            parameters.Add(("takenBefore", Fmt(filters.TakenBefore.Value)));
        }
        AddIfInt(conditions, parameters, "t.width >= $minW", filters.MinWidth, "minW");
        AddIfInt(conditions, parameters, "t.width <= $maxW", filters.MaxWidth, "maxW");
        AddIfInt(conditions, parameters, "t.height >= $minH", filters.MinHeight, "minH");
        AddIfInt(conditions, parameters, "t.height <= $maxH", filters.MaxHeight, "maxH");
        AddIfLong(conditions, parameters, "t.file_size >= $minFS", filters.MinFileSize, "minFS");
        AddIfLong(conditions, parameters, "t.file_size <= $maxFS", filters.MaxFileSize, "maxFS");
        if (!string.IsNullOrEmpty(filters.CameraBrand))
        {
            conditions.Add("LOWER(t.camera) LIKE $cam");
            parameters.Add(("cam", "%" + filters.CameraBrand.ToLowerInvariant() + "%"));
        }
        if (filters.TagId is > 0)
        {
            conditions.Add("EXISTS (SELECT 1 FROM image_tags it WHERE it.image_id = t.id AND it.tag_id = $tagId)");
            parameters.Add(("tagId", filters.TagId.Value));
        }

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var sql = $"SELECT COUNT(*) FROM {FromClause(filters)} {whereClause}";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        using var registration = cancellationToken.Register(cmd.Cancel);
        cancellationToken.ThrowIfCancellationRequested();
        return (int)(long)cmd.ExecuteScalar()!;
    }

    private static void AddFilterConditions(SearchFilters filters, List<string> conditions, List<(string Name, object Value)> parameters)
    {
        if (!string.IsNullOrWhiteSpace(filters.Query))
        {
            AddTextCondition(filters.Query, conditions, parameters);
        }
        AddGeoConditions(filters, conditions, parameters);
        if (filters.SourceId is > 0) { conditions.Add("t.source_id = $sourceId"); parameters.Add(("sourceId", filters.SourceId.Value)); }
        if (!filters.IncludeMissing) conditions.Add("t.status = 'active'");
        if (filters.TakenAfter.HasValue) { conditions.Add("t.taken_utc >= $takenAfter"); parameters.Add(("takenAfter", Fmt(filters.TakenAfter.Value))); }
        if (filters.TakenBefore.HasValue) { conditions.Add("t.taken_utc <= $takenBefore"); parameters.Add(("takenBefore", Fmt(filters.TakenBefore.Value))); }
        AddIfInt(conditions, parameters, "t.width >= $minW", filters.MinWidth, "minW");
        AddIfInt(conditions, parameters, "t.width <= $maxW", filters.MaxWidth, "maxW");
        AddIfInt(conditions, parameters, "t.height >= $minH", filters.MinHeight, "minH");
        AddIfInt(conditions, parameters, "t.height <= $maxH", filters.MaxHeight, "maxH");
        AddIfLong(conditions, parameters, "t.file_size >= $minFS", filters.MinFileSize, "minFS");
        AddIfLong(conditions, parameters, "t.file_size <= $maxFS", filters.MaxFileSize, "maxFS");
        if (!string.IsNullOrWhiteSpace(filters.CameraBrand))
        {
            conditions.Add("LOWER(t.camera) LIKE $cam");
            parameters.Add(("cam", "%" + filters.CameraBrand.ToLowerInvariant() + "%"));
        }
        if (filters.TagId is > 0)
        {
            conditions.Add("EXISTS (SELECT 1 FROM image_tags it WHERE it.image_id = t.id AND it.tag_id = $tagId)");
            parameters.Add(("tagId", filters.TagId.Value));
        }
        if (filters.PersonId is > 0)
        {
            conditions.Add("EXISTS (SELECT 1 FROM detected_faces pf JOIN face_person_assignments pa ON pa.face_id=pf.id WHERE pf.image_id=t.id AND pa.person_id=$personId AND pa.state='confirmed')");
            parameters.Add(("personId", filters.PersonId.Value));
        }
        AddVisualConceptConditions(filters, conditions, parameters);
    }

    private static void AddVisualConceptConditions(SearchFilters filters, List<string> conditions,
        List<(string Name, object Value)> parameters)
    {
        var concepts = filters.VisualConceptIds.Distinct(StringComparer.Ordinal).ToArray();
        for (var index = 0; index < concepts.Length; index++)
        {
            var parameter = $"visualConcept{index}";
            conditions.Add($"EXISTS (SELECT 1 FROM image_visual_concepts vc{index} WHERE vc{index}.image_id=t.id AND vc{index}.concept_id=${parameter} AND vc{index}.state IN ('suggested','accepted'))");
            parameters.Add((parameter, concepts[index]));
        }
    }

    private static SearchCursor CreateCursor(ImageRecord record, SortField field) => new(field switch
    {
        SortField.FileName => record.Path,
        SortField.TakenDate => Fmt(record.TakenUtc ?? record.IndexedUtc),
        SortField.Width => record.Width,
        SortField.Height => record.Height,
        SortField.FileSize => record.FileSize,
        SortField.IndexedTime => Fmt(record.IndexedUtc),
        _ => record.Path
    }, record.Id);

    private static string FromClause(SearchFilters filters) => "images t";

    private static void AddTextCondition(string query, List<string> conditions, List<(string Name, object Value)> parameters)
    {
        conditions.Add("""
            (t.id IN (SELECT rowid FROM images_fts WHERE images_fts MATCH $ftsQuery)
             OR EXISTS (SELECT 1 FROM cloud_vision_analyses cv
                        WHERE cv.image_id=t.id AND cv.status='completed'
                          AND (cv.summary LIKE $cloudQuery OR cv.result_json LIKE $cloudQuery))
             OR EXISTS (SELECT 1 FROM florence_image_results fr
                        WHERE fr.image_id=t.id AND fr.state='accepted' AND fr.result_text LIKE $cloudQuery)
             OR EXISTS (SELECT 1 FROM image_locations il
                        WHERE il.image_id=t.id AND il.search_text LIKE $cloudQuery)
             OR EXISTS (SELECT 1 FROM image_tags it JOIN tags tag ON tag.id=it.tag_id
                        WHERE it.image_id=t.id AND tag.name LIKE $cloudQuery)
             OR EXISTS (SELECT 1 FROM scene_group_descriptions sg, json_each(sg.image_ids_json) member
                        WHERE member.value=t.id AND sg.state='accepted'
                          AND (sg.scene_name LIKE $cloudQuery OR sg.summary LIKE $cloudQuery OR sg.search_terms_json LIKE $cloudQuery)))
            """);
        parameters.Add(("ftsQuery", BuildFtsQuery(query)));
        parameters.Add(("cloudQuery", "%" + query.Trim() + "%"));
    }

    private static string SortColumn(SortField field) => field switch
    {
        SortField.FileName       => "t.path",
        SortField.TakenDate      => "COALESCE(t.taken_utc, t.file_created_utc, t.modified_utc)",
        SortField.Width          => "t.width",
        SortField.Height         => "t.height",
        SortField.FileSize       => "t.file_size",
        SortField.IndexedTime    => "t.indexed_utc",
        _                        => "t.path"
    };

    private static void AddIfInt(List<string> conds, List<(string N, object V)> parms, string sql, int? val, string paramName)
    {
        if (val.HasValue)
        {
            conds.Add(sql);
            parms.Add((paramName, val.Value));
        }
    }

    private static void AddGeoConditions(SearchFilters filters, List<string> conditions, List<(string Name, object Value)> parameters)
    {
        if (filters.GeoBounds is not { } bounds) return;
        if (bounds.AreaKey is not null)
        {
            conditions.Add("""
                (EXISTS (SELECT 1 FROM image_locations il WHERE il.image_id=t.id AND il.search_text LIKE $geoName)
                 OR (NOT EXISTS (SELECT 1 FROM image_locations il WHERE il.image_id=t.id)
                     AND gps_in_area(t.gps, $areaKey) = 1))
                """);
            parameters.Add(("geoName", "%" + bounds.Name + "%"));
            parameters.Add(("areaKey", bounds.AreaKey));
            return;
        }
        conditions.Add("gps_in_bounds(t.gps, $minLat, $maxLat, $minLon, $maxLon) = 1");
        parameters.Add(("minLat", bounds.MinLatitude));
        parameters.Add(("maxLat", bounds.MaxLatitude));
        parameters.Add(("minLon", bounds.MinLongitude));
        parameters.Add(("maxLon", bounds.MaxLongitude));
    }

    private static void AddIfLong(List<string> conds, List<(string N, object V)> parms, string sql, long? val, string paramName)
    {
        if (val.HasValue)
        {
            conds.Add(sql);
            parms.Add((paramName, val.Value));
        }
    }

    private static string Fmt(DateTime dt) => dt.ToUniversalTime().ToString("o");

    private static string BuildFtsQuery(string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.Replace("\"", "\"\""))
            .Where(term => term.Length > 0)
            .Select(term => $"\"{term}\"*");
        return string.Join(" AND ", terms);
    }

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
            var ordCreated = reader.GetOrdinal("file_created_utc");

            list.Add(new ImageRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                SourceId = reader.GetInt64(reader.GetOrdinal("source_id")),
                Path = reader.GetString(reader.GetOrdinal("path")),
                FileSize = reader.GetInt64(reader.GetOrdinal("file_size")),
                ModifiedUtc = Parse(reader.GetString(ordModified)),
                CreatedUtc = IsDBNull(reader, ordCreated) ? null : Parse(reader.GetString(ordCreated)),
                Sha256 = IsDBNull(reader, ordSha) ? null : reader.GetString(ordSha),
                Width = reader.GetInt32(reader.GetOrdinal("width")),
                Height = reader.GetInt32(reader.GetOrdinal("height")),
                TakenUtc = IsDBNull(reader, ordTaken) ? null : Parse(reader.GetString(ordTaken)),
                Camera = IsDBNull(reader, ordCam) ? null : reader.GetString(ordCam),
                Gps = IsDBNull(reader, ordGps) ? null : reader.GetString(ordGps),
                ThumbPath = IsDBNull(reader, ordThumb) ? null : reader.GetString(ordThumb),
                Status = reader.GetString(reader.GetOrdinal("status")) == "active" ? ImageStatus.Active : ImageStatus.Missing,
                IndexedUtc = Parse(reader.GetOrdinal("indexed_utc") >= 0 ? reader.GetString(reader.GetOrdinal("indexed_utc")) : Fmt(DateTime.UtcNow))
            });
        }
        return list;
    }

    private static bool IsDBNull(SqliteDataReader r, int i) => r.IsDBNull(i);
    private static DateTime Parse(string s) =>
        DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
}
