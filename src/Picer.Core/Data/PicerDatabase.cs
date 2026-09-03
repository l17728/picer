using Microsoft.Data.Sqlite;
using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.Core.Data;

public sealed class PicerDatabase : IDisposable
{
    public const int CurrentSchemaVersion = 21;
    private readonly SqliteConnection _conn;
    private readonly string _connectionString;

    public PicerDatabase(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            ForeignKeys = true,
            DefaultTimeout = 30
        }.ToString();

        _conn = new SqliteConnection(_connectionString);
        _conn.Open();
        RegisterFunctions(_conn);

        using (var currentVersion = _conn.CreateCommand())
        {
            currentVersion.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(currentVersion.ExecuteScalar()) >= CurrentSchemaVersion)
                return;
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS sources (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                path      TEXT NOT NULL UNIQUE,
                added_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS images (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id   INTEGER NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
                path        TEXT NOT NULL UNIQUE,
                file_size   INTEGER NOT NULL,
                modified_utc TEXT NOT NULL,
                sha256      TEXT,
                width       INTEGER NOT NULL DEFAULT 0,
                height      INTEGER NOT NULL DEFAULT 0,
                taken_utc   TEXT,
                camera      TEXT,
                gps         TEXT,
                thumb_path  TEXT,
                status      TEXT NOT NULL DEFAULT 'active',
                indexed_utc TEXT NOT NULL,
                ahash       INTEGER NOT NULL DEFAULT 0,
                dhash       INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_images_sha    ON images(sha256);
            CREATE INDEX IF NOT EXISTS idx_images_source ON images(source_id);
            CREATE INDEX IF NOT EXISTS idx_images_status ON images(status);
            CREATE INDEX IF NOT EXISTS idx_images_status_taken_id ON images(status, taken_utc DESC, id DESC);
            DROP INDEX IF EXISTS idx_images_status_path_id;
            DROP INDEX IF EXISTS idx_images_source_status_taken_id;
            DROP INDEX IF EXISTS idx_images_status_size_id;
            DROP INDEX IF EXISTS idx_images_status_dimensions;

            CREATE TABLE IF NOT EXISTS tags (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT NOT NULL UNIQUE,
                created_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS image_tags (
                image_id  INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                tag_id    INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                PRIMARY KEY (image_id, tag_id)
            );

            CREATE TABLE IF NOT EXISTS albums (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT NOT NULL,
                creator_id  INTEGER,
                created_utc TEXT NOT NULL,
                description TEXT
            );

            CREATE TABLE IF NOT EXISTS album_images (
                album_id  INTEGER NOT NULL REFERENCES albums(id) ON DELETE CASCADE,
                image_id  INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                added_utc TEXT NOT NULL,
                PRIMARY KEY (album_id, image_id)
            );

            CREATE TABLE IF NOT EXISTS rejected_suggestions (
                image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                tag_name TEXT NOT NULL,
                rejected_utc TEXT NOT NULL,
                PRIMARY KEY (image_id, tag_name)
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS images_fts USING fts5(
                filename, camera, gps, width, height, file_size
            );

            DROP TRIGGER IF EXISTS images_ai;
            DROP TRIGGER IF EXISTS images_ad;
            DROP TRIGGER IF EXISTS images_au;

            CREATE TRIGGER images_ai AFTER INSERT ON images BEGIN
                INSERT INTO images_fts(rowid, filename, camera, gps, width, height, file_size)
                VALUES (new.id, new.path, new.camera, new.gps, new.width, new.height, new.file_size);
            END;

            CREATE TRIGGER images_ad AFTER DELETE ON images BEGIN
                DELETE FROM images_fts WHERE rowid = old.id;
            END;

            CREATE TRIGGER images_au AFTER UPDATE ON images BEGIN
                DELETE FROM images_fts WHERE rowid = old.id;
                INSERT INTO images_fts(rowid, filename, camera, gps, width, height, file_size)
                VALUES (new.id, new.path, new.camera, new.gps, new.width, new.height, new.file_size);
            END;
            """;
        cmd.ExecuteNonQuery();

        using var version = _conn.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var schemaVersion = Convert.ToInt32(version.ExecuteScalar());
        if (schemaVersion < 2)
        {
            using var migrate = _conn.CreateCommand();
            migrate.CommandText = """
                DROP TABLE IF EXISTS images_fts;
                CREATE VIRTUAL TABLE images_fts USING fts5(filename, camera, gps, width, height, file_size);
                INSERT INTO images_fts(rowid, filename, camera, gps, width, height, file_size)
                SELECT id, path, camera, gps, width, height, file_size FROM images;
                PRAGMA user_version = 2;
                """;
            migrate.ExecuteNonQuery();
        }
        if (schemaVersion < 3)
        {
            using var finishMigration = _conn.CreateCommand();
            finishMigration.CommandText = "PRAGMA user_version = 3";
            finishMigration.ExecuteNonQuery();
        }

        using var visionMigration = _conn.CreateCommand();
        visionMigration.CommandText = """
            CREATE TABLE IF NOT EXISTS vision_analyses (
                image_id            INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                analyzer            TEXT NOT NULL,
                model_version       TEXT NOT NULL,
                source_file_size    INTEGER NOT NULL,
                source_modified_utc TEXT NOT NULL,
                status              TEXT NOT NULL,
                face_count          INTEGER NOT NULL DEFAULT 0,
                error_code          TEXT,
                analyzed_utc        TEXT NOT NULL,
                PRIMARY KEY (image_id, analyzer)
            );

            CREATE TABLE IF NOT EXISTS detected_faces (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                image_id       INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                model_version  TEXT NOT NULL,
                ordinal        INTEGER NOT NULL,
                box_x          REAL NOT NULL,
                box_y          REAL NOT NULL,
                box_width      REAL NOT NULL,
                box_height     REAL NOT NULL,
                confidence     REAL NOT NULL,
                landmarks_json TEXT,
                embedding      BLOB,
                created_utc    TEXT NOT NULL,
                UNIQUE (image_id, model_version, ordinal)
            );

            CREATE INDEX IF NOT EXISTS idx_detected_faces_image ON detected_faces(image_id);
            CREATE INDEX IF NOT EXISTS idx_vision_analyses_status ON vision_analyses(status, analyzer);

            CREATE TABLE IF NOT EXISTS people (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                display_name TEXT NOT NULL,
                created_utc  TEXT NOT NULL,
                updated_utc  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS person_aliases (
                person_id  INTEGER NOT NULL REFERENCES people(id) ON DELETE CASCADE,
                alias      TEXT NOT NULL COLLATE NOCASE UNIQUE,
                created_utc TEXT NOT NULL,
                PRIMARY KEY (person_id, alias)
            );

            CREATE TABLE IF NOT EXISTS face_person_assignments (
                face_id      INTEGER PRIMARY KEY REFERENCES detected_faces(id) ON DELETE CASCADE,
                person_id    INTEGER NOT NULL REFERENCES people(id) ON DELETE CASCADE,
                state        TEXT NOT NULL CHECK (state IN ('suggested', 'confirmed')),
                confidence   REAL,
                assigned_by  TEXT NOT NULL CHECK (assigned_by IN ('cluster', 'user')),
                updated_utc  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS face_person_rejections (
                face_id      INTEGER NOT NULL REFERENCES detected_faces(id) ON DELETE CASCADE,
                person_id    INTEGER NOT NULL REFERENCES people(id) ON DELETE CASCADE,
                created_utc  TEXT NOT NULL,
                PRIMARY KEY (face_id, person_id)
            );

            CREATE TABLE IF NOT EXISTS face_corrections (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                face_id        INTEGER NOT NULL REFERENCES detected_faces(id) ON DELETE CASCADE,
                old_person_id  INTEGER REFERENCES people(id) ON DELETE SET NULL,
                new_person_id  INTEGER REFERENCES people(id) ON DELETE SET NULL,
                action         TEXT NOT NULL,
                created_utc    TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_face_assignments_person ON face_person_assignments(person_id, state);
            PRAGMA user_version = 4;
            """;
        visionMigration.ExecuteNonQuery();

        using var clusterMigration = _conn.CreateCommand();
        clusterMigration.CommandText = """
            CREATE TABLE IF NOT EXISTS face_candidate_clusters (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                model_version TEXT NOT NULL,
                created_utc   TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS face_candidate_members (
                cluster_id INTEGER NOT NULL REFERENCES face_candidate_clusters(id) ON DELETE CASCADE,
                face_id    INTEGER NOT NULL REFERENCES detected_faces(id) ON DELETE CASCADE,
                similarity REAL NOT NULL,
                PRIMARY KEY (cluster_id, face_id),
                UNIQUE (face_id)
            );

            CREATE INDEX IF NOT EXISTS idx_face_candidate_members_cluster ON face_candidate_members(cluster_id);
            PRAGMA user_version = 5;
            """;
        clusterMigration.ExecuteNonQuery();

        using var visualMigration = _conn.CreateCommand();
        visualMigration.CommandText = """
            CREATE TABLE IF NOT EXISTS image_visual_concepts (
                image_id      INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                concept_id    TEXT NOT NULL,
                model_version TEXT NOT NULL,
                confidence    REAL NOT NULL,
                state         TEXT NOT NULL CHECK (state IN ('suggested','accepted')),
                created_utc   TEXT NOT NULL,
                PRIMARY KEY (image_id, concept_id, model_version)
            );

            CREATE TABLE IF NOT EXISTS visual_concept_rejections (
                image_id    INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                concept_id  TEXT NOT NULL,
                rejected_utc TEXT NOT NULL,
                PRIMARY KEY (image_id, concept_id)
            );

            CREATE INDEX IF NOT EXISTS idx_visual_concepts_lookup
                ON image_visual_concepts(concept_id, state, image_id);
            PRAGMA user_version = 6;
            """;
        visualMigration.ExecuteNonQuery();

        using var cloudVisionMigration = _conn.CreateCommand();
        cloudVisionMigration.CommandText = """
            CREATE TABLE IF NOT EXISTS cloud_vision_analyses (
                image_id             INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                provider             TEXT NOT NULL,
                model_version        TEXT NOT NULL,
                prompt_version       TEXT NOT NULL,
                source_file_size     INTEGER NOT NULL,
                source_modified_utc  TEXT NOT NULL,
                location_fingerprint TEXT NOT NULL,
                status               TEXT NOT NULL CHECK (status IN ('queued','processing','completed','failed')),
                reason               TEXT,
                summary              TEXT,
                result_json          TEXT,
                error_code           TEXT,
                updated_utc           TEXT NOT NULL,
                PRIMARY KEY (image_id, provider)
            );

            CREATE INDEX IF NOT EXISTS idx_cloud_vision_queue
                ON cloud_vision_analyses(status, updated_utc);
            PRAGMA user_version = 7;
            """;
        cloudVisionMigration.ExecuteNonQuery();

        if (schemaVersion < 8)
        {
            AddColumnIfMissing("cloud_vision_analyses", "input_tokens", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("cloud_vision_analyses", "output_tokens", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("cloud_vision_analyses", "estimated_cost_yuan", "REAL NOT NULL DEFAULT 0");
            using var cloudUsageMigration = _conn.CreateCommand();
            cloudUsageMigration.CommandText = """
            PRAGMA user_version = 8;
            """;
            cloudUsageMigration.ExecuteNonQuery();
        }

        if (schemaVersion < 9)
        {
            using var videoMigration = _conn.CreateCommand();
            videoMigration.CommandText = """
            CREATE TABLE IF NOT EXISTS videos (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id INTEGER NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
                path TEXT NOT NULL UNIQUE,
                file_size INTEGER NOT NULL,
                modified_utc TEXT NOT NULL,
                duration_seconds REAL,
                width INTEGER NOT NULL DEFAULT 0,
                height INTEGER NOT NULL DEFAULT 0,
                taken_utc TEXT,
                gps TEXT,
                status TEXT NOT NULL DEFAULT 'active',
                indexed_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS video_keyframes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                video_id INTEGER NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
                timestamp_seconds REAL NOT NULL,
                path TEXT NOT NULL,
                dhash INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL,
                UNIQUE(video_id,timestamp_seconds)
            );
            CREATE TABLE IF NOT EXISTS video_transcripts (
                video_id INTEGER PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE,
                model_version TEXT NOT NULL,
                transcript_json TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_videos_source_status ON videos(source_id,status);
            CREATE INDEX IF NOT EXISTS idx_video_keyframes_video ON video_keyframes(video_id,timestamp_seconds);
            PRAGMA user_version = 9;
            """;
            videoMigration.ExecuteNonQuery();
        }
        if (schemaVersion < 10)
        {
            using var videoFaceMigration = _conn.CreateCommand();
            videoFaceMigration.CommandText = """
                CREATE TABLE IF NOT EXISTS video_detected_faces (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    video_id INTEGER NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
                    keyframe_id INTEGER NOT NULL REFERENCES video_keyframes(id) ON DELETE CASCADE,
                    timestamp_seconds REAL NOT NULL,
                    ordinal INTEGER NOT NULL,
                    box_x REAL NOT NULL, box_y REAL NOT NULL, box_width REAL NOT NULL, box_height REAL NOT NULL,
                    confidence REAL NOT NULL, embedding BLOB,
                    suggested_person_id INTEGER REFERENCES people(id) ON DELETE SET NULL,
                    match_confidence REAL,
                    created_utc TEXT NOT NULL,
                    UNIQUE(keyframe_id,ordinal)
                );
                CREATE INDEX IF NOT EXISTS idx_video_faces_timeline ON video_detected_faces(video_id,timestamp_seconds);
                PRAGMA user_version = 10;
                """;
            videoFaceMigration.ExecuteNonQuery();
        }
        if (schemaVersion < 11)
        {
            if (!ColumnExists("images", "file_created_utc"))
            {
                using var addCreatedTime = _conn.CreateCommand();
                addCreatedTime.CommandText = "ALTER TABLE images ADD COLUMN file_created_utc TEXT";
                addCreatedTime.ExecuteNonQuery();
            }
            using var createdTimeMigration = _conn.CreateCommand();
            createdTimeMigration.CommandText = """
                UPDATE images SET file_created_utc=modified_utc WHERE file_created_utc IS NULL;
                CREATE INDEX IF NOT EXISTS idx_images_effective_time ON images(status,COALESCE(taken_utc,file_created_utc,modified_utc),id);
                PRAGMA user_version = 11;
                """;
            createdTimeMigration.ExecuteNonQuery();
        }
        if(schemaVersion<12)
        {
            using var videoSummaryMigration=_conn.CreateCommand();
            videoSummaryMigration.CommandText="""
                CREATE TABLE IF NOT EXISTS video_summaries(
                    video_id INTEGER PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE,
                    provider TEXT NOT NULL, model_version TEXT NOT NULL, prompt_version TEXT NOT NULL,
                    source_file_size INTEGER NOT NULL, source_modified_utc TEXT NOT NULL,
                    summary TEXT NOT NULL, result_json TEXT NOT NULL,
                    input_tokens INTEGER NOT NULL DEFAULT 0, output_tokens INTEGER NOT NULL DEFAULT 0,
                    estimated_cost_yuan REAL NOT NULL DEFAULT 0, created_utc TEXT NOT NULL
                );
                PRAGMA user_version=12;
                """;videoSummaryMigration.ExecuteNonQuery();
        }
        if (schemaVersion < 13)
        {
            using var validationMigration = _conn.CreateCommand();
            validationMigration.CommandText = """
                CREATE TABLE IF NOT EXISTS face_detection_ground_truth(
                    image_id INTEGER PRIMARY KEY REFERENCES images(id) ON DELETE CASCADE,
                    expected_face_count INTEGER NOT NULL CHECK(expected_face_count >= 0),
                    categories TEXT NOT NULL DEFAULT '',
                    notes TEXT,
                    updated_utc TEXT NOT NULL
                );
                PRAGMA user_version = 13;
                """;
            validationMigration.ExecuteNonQuery();
        }
        if (schemaVersion < 14)
        {
            using var thresholdMigration = _conn.CreateCommand();
            thresholdMigration.CommandText = """
                CREATE TABLE IF NOT EXISTS face_model_settings(
                    model_version TEXT PRIMARY KEY,
                    match_threshold REAL NOT NULL CHECK(match_threshold >= 0 AND match_threshold <= 1),
                    calibrated_same_pairs INTEGER NOT NULL DEFAULT 0,
                    calibrated_different_pairs INTEGER NOT NULL DEFAULT 0,
                    updated_utc TEXT NOT NULL
                );
                PRAGMA user_version = 14;
                """;
            thresholdMigration.ExecuteNonQuery();
        }
        if (schemaVersion < 15)
        {
            using var clipMigration = _conn.CreateCommand();
            clipMigration.CommandText = """
                CREATE TABLE IF NOT EXISTS clip_image_embeddings(
                    image_id INTEGER PRIMARY KEY REFERENCES images(id) ON DELETE CASCADE,
                    model_version TEXT NOT NULL,
                    source_file_size INTEGER NOT NULL,
                    source_modified_utc TEXT NOT NULL,
                    embedding BLOB NOT NULL,
                    indexed_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS clip_query_evaluations(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    query TEXT NOT NULL,
                    model_version TEXT NOT NULL,
                    expected_relevant_count INTEGER,
                    created_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS clip_query_results(
                    evaluation_id INTEGER NOT NULL REFERENCES clip_query_evaluations(id) ON DELETE CASCADE,
                    image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                    rank INTEGER NOT NULL,
                    score REAL NOT NULL,
                    relevant INTEGER,
                    PRIMARY KEY(evaluation_id,image_id)
                );
                CREATE INDEX IF NOT EXISTS idx_clip_evaluation_results ON clip_query_results(evaluation_id,rank);
                PRAGMA user_version = 15;
                """;
            clipMigration.ExecuteNonQuery();
        }
        if (schemaVersion < 16)
        {
            using var florenceMigration = _conn.CreateCommand();
            florenceMigration.CommandText = """
                CREATE TABLE IF NOT EXISTS florence_image_results(
                    image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                    task TEXT NOT NULL,
                    model_version TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    source_file_size INTEGER NOT NULL,
                    source_modified_utc TEXT NOT NULL,
                    result_text TEXT NOT NULL,
                    state TEXT NOT NULL CHECK(state IN ('suggested','accepted','rejected')),
                    created_utc TEXT NOT NULL,
                    reviewed_utc TEXT,
                    PRIMARY KEY(image_id,task)
                );
                CREATE INDEX IF NOT EXISTS idx_florence_results_state ON florence_image_results(state,image_id);
                PRAGMA user_version = 16;
                """;
            florenceMigration.ExecuteNonQuery();
        }
        if (schemaVersion < 17)
        {
            using var sceneDescriptionMigration = _conn.CreateCommand();
            sceneDescriptionMigration.CommandText = """
                CREATE TABLE IF NOT EXISTS scene_group_descriptions(
                    fingerprint TEXT PRIMARY KEY,
                    directory_path TEXT NOT NULL,
                    start_utc TEXT NOT NULL,
                    end_utc TEXT NOT NULL,
                    image_ids_json TEXT NOT NULL,
                    representative_image_ids_json TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    model_version TEXT NOT NULL,
                    prompt_version TEXT NOT NULL,
                    scene_name TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    search_terms_json TEXT NOT NULL,
                    state TEXT NOT NULL CHECK(state IN ('accepted','rejected')),
                    created_utc TEXT NOT NULL,
                    reviewed_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_scene_group_directory_time
                    ON scene_group_descriptions(directory_path,start_utc,state);
                PRAGMA user_version = 17;
                """;
            sceneDescriptionMigration.ExecuteNonQuery();
        }
        if (schemaVersion < 18)
        {
            if (!ColumnExists("people", "description"))
            {
                using var addDescription = _conn.CreateCommand();
                addDescription.CommandText = "ALTER TABLE people ADD COLUMN description TEXT NOT NULL DEFAULT ''";
                addDescription.ExecuteNonQuery();
            }
            using var finishPeopleDescription = _conn.CreateCommand();
            finishPeopleDescription.CommandText = "PRAGMA user_version = 18";
            finishPeopleDescription.ExecuteNonQuery();
        }
        if (schemaVersion < 19)
        {
            using var faceReviewMigration = _conn.CreateCommand();
            faceReviewMigration.CommandText = """
                CREATE TABLE IF NOT EXISTS face_review_states(
                    face_id INTEGER PRIMARY KEY REFERENCES detected_faces(id) ON DELETE CASCADE,
                    state TEXT NOT NULL CHECK(state IN ('ignored')),
                    updated_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_face_review_states_state ON face_review_states(state,face_id);
                PRAGMA user_version = 19;
                """;
            faceReviewMigration.ExecuteNonQuery();
        }
        if (schemaVersion < 20)
        {
            using var locationHierarchyMigration = _conn.CreateCommand();
            locationHierarchyMigration.CommandText = """
                CREATE TABLE IF NOT EXISTS image_locations(
                    image_id INTEGER PRIMARY KEY REFERENCES images(id) ON DELETE CASCADE,
                    latitude REAL NOT NULL,
                    longitude REAL NOT NULL,
                    country TEXT,
                    province TEXT,
                    city TEXT,
                    county TEXT,
                    district TEXT,
                    town TEXT,
                    village TEXT,
                    display_name TEXT NOT NULL,
                    search_text TEXT NOT NULL,
                    resolved_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_image_locations_country ON image_locations(country);
                CREATE INDEX IF NOT EXISTS idx_image_locations_province ON image_locations(province);
                CREATE INDEX IF NOT EXISTS idx_image_locations_city ON image_locations(city);
                CREATE INDEX IF NOT EXISTS idx_image_locations_county ON image_locations(county);
                CREATE INDEX IF NOT EXISTS idx_image_locations_district ON image_locations(district);
                CREATE INDEX IF NOT EXISTS idx_image_locations_town ON image_locations(town);
                CREATE INDEX IF NOT EXISTS idx_image_locations_village ON image_locations(village);
                PRAGMA user_version = 20;
                """;
            locationHierarchyMigration.ExecuteNonQuery();
        }
        if (schemaVersion < 21)
        {
            AddColumnIfMissing("video_detected_faces", "assigned_person_id", "INTEGER REFERENCES people(id) ON DELETE SET NULL");
            AddColumnIfMissing("video_detected_faces", "assignment_state", "TEXT CHECK(assignment_state IN ('confirmed','ignored'))");
            AddColumnIfMissing("video_detected_faces", "updated_utc", "TEXT");
            using var videoFaceReviewMigration = _conn.CreateCommand();
            videoFaceReviewMigration.CommandText = """
                CREATE TABLE IF NOT EXISTS video_face_person_rejections(
                    video_face_id INTEGER NOT NULL REFERENCES video_detected_faces(id) ON DELETE CASCADE,
                    person_id INTEGER NOT NULL REFERENCES people(id) ON DELETE CASCADE,
                    created_utc TEXT NOT NULL,
                    PRIMARY KEY(video_face_id,person_id)
                );
                CREATE INDEX IF NOT EXISTS idx_video_faces_assigned_person
                    ON video_detected_faces(assigned_person_id,assignment_state);
                PRAGMA user_version = 21;
                """;
            videoFaceReviewMigration.ExecuteNonQuery();
        }
    }

    public SqliteCommand Command(string sql)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    private bool ColumnExists(string table, string column)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = $"PRAGMA table_info([{table.Replace("]", "]]", StringComparison.Ordinal)}])";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void AddColumnIfMissing(string table, string column, string definition)
    {
        if (ColumnExists(table, column)) return;
        using var command = _conn.CreateCommand();
        command.CommandText = $"ALTER TABLE [{table.Replace("]", "]]", StringComparison.Ordinal)}] ADD COLUMN [{column.Replace("]", "]]", StringComparison.Ordinal)}] {definition}";
        command.ExecuteNonQuery();
    }

    private static string Fmt(DateTime dt) => dt.ToUniversalTime().ToString("o");

    private static DateTime Parse(string s) =>
        DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    public SourceFolder AddSource(string path)
    {
        path = Path.GetFullPath(path);
        using var select = Command("SELECT id, path, added_utc FROM sources WHERE path = $p");
        select.Parameters.AddWithValue("$p", path);
        using (var r = select.ExecuteReader())
        {
            if (r.Read())
                return new SourceFolder
                {
                    Id = r.GetInt64(0),
                    Path = r.GetString(1),
                    AddedUtc = Parse(r.GetString(2))
                };
        }

        using var insert = Command("INSERT INTO sources (path, added_utc) VALUES ($p, $a); SELECT last_insert_rowid();");
        insert.Parameters.AddWithValue("$p", path);
        insert.Parameters.AddWithValue("$a", Fmt(DateTime.UtcNow));
        var id = (long)insert.ExecuteScalar()!;
        return new SourceFolder { Id = id, Path = path, AddedUtc = DateTime.UtcNow };
    }

    public bool RemoveSource(long id)
    {
        using var cmd = Command("DELETE FROM sources WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public List<SourceFolder> GetSources()
    {
        using var cmd = Command("""
            SELECT s.id, s.path, s.added_utc, COUNT(i.id)
            FROM sources s
            LEFT JOIN images i ON i.source_id = s.id AND i.status = 'active'
            GROUP BY s.id
            ORDER BY s.path
            """);
        var list = new List<SourceFolder>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SourceFolder
            {
                Id = r.GetInt64(0),
                Path = r.GetString(1),
                AddedUtc = Parse(r.GetString(2)),
                ImageCount = (int)r.GetInt64(3)
            });
        }
        return list;
    }

    public List<ImageLocationHierarchyRow> GetImageLocationHierarchyRows()
    {
        using var connection = OpenReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id, CASE WHEN i.gps IS NOT NULL AND i.gps<>'' THEN 1 ELSE 0 END,
                   l.country,l.province,l.city,l.county,l.district,l.town,l.village
            FROM images i
            LEFT JOIN image_locations l ON l.image_id=i.id
            WHERE i.status='active'
            ORDER BY COALESCE(l.country,''),COALESCE(l.province,''),COALESCE(l.city,''),i.id
            """;
        var rows = new List<ImageLocationHierarchyRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new(
                reader.GetInt64(0), reader.GetInt64(1) == 1,
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        return rows;
    }

    public List<LocationHierarchyChoice> GetLocationHierarchyChoices()
    {
        var choices = new Dictionary<string, LocationHierarchyChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in GetImageLocationHierarchyRows().Where(row => !string.IsNullOrWhiteSpace(row.Country)))
        {
            string?[] values = [row.Country, row.Province, row.City, row.County, row.District, row.Town, row.Village];
            var current = new string?[7];
            for (var index = 0; index < values.Length; index++)
            {
                if (string.IsNullOrWhiteSpace(values[index])) continue;
                current[index] = values[index]!.Trim();
                var display = string.Join(" / ", current.Where(value => !string.IsNullOrWhiteSpace(value)));
                choices.TryAdd(display, new(display, current[0], current[1], current[2], current[3], current[4], current[5], current[6]));
            }
        }
        return choices.Values.OrderBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void SetImageLocation(long imageId, LocationHierarchyChoice location)
    {
        SetImageLocations([imageId], location);
    }

    public void SetImageLocations(IEnumerable<long> imageIds, LocationHierarchyChoice location)
    {
        var ids = imageIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var display = string.Join("，", new[] { location.Village, location.Town, location.District, location.County,
            location.City, location.Province, location.Country }.Where(value => !string.IsNullOrWhiteSpace(value)));
        foreach (var imageId in ids)
        {
            using var coordinates = connection.CreateCommand();
            coordinates.Transaction = transaction;
            coordinates.CommandText = "SELECT COALESCE(gps,'') FROM images WHERE id=$image";
            coordinates.Parameters.AddWithValue("$image", imageId);
            var gpsValue = coordinates.ExecuteScalar();
            if (gpsValue is null) throw new InvalidOperationException($"照片索引不存在：{imageId}");
            var gps = (string)gpsValue;
            var latitude = 0d;
            var longitude = 0d;
            GeoPlaceResolver.TryParseCoordinates(gps, out latitude, out longitude);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO image_locations(image_id,latitude,longitude,country,province,city,county,district,town,village,display_name,search_text,resolved_utc)
                VALUES($image,$latitude,$longitude,$country,$province,$city,$county,$district,$town,$village,$display,$display,$now)
                ON CONFLICT(image_id) DO UPDATE SET country=excluded.country,province=excluded.province,city=excluded.city,
                    county=excluded.county,district=excluded.district,town=excluded.town,village=excluded.village,
                    display_name=excluded.display_name,search_text=excluded.search_text,resolved_utc=excluded.resolved_utc
                """;
            command.Parameters.AddWithValue("$image", imageId);
            command.Parameters.AddWithValue("$latitude", latitude);
            command.Parameters.AddWithValue("$longitude", longitude);
            command.Parameters.AddWithValue("$country", (object?)location.Country ?? DBNull.Value);
            command.Parameters.AddWithValue("$province", (object?)location.Province ?? DBNull.Value);
            command.Parameters.AddWithValue("$city", (object?)location.City ?? DBNull.Value);
            command.Parameters.AddWithValue("$county", (object?)location.County ?? DBNull.Value);
            command.Parameters.AddWithValue("$district", (object?)location.District ?? DBNull.Value);
            command.Parameters.AddWithValue("$town", (object?)location.Town ?? DBNull.Value);
            command.Parameters.AddWithValue("$village", (object?)location.Village ?? DBNull.Value);
            command.Parameters.AddWithValue("$display", display);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public SourceFolder? GetSource(long id)
    {
        using var cmd = Command("SELECT id, path, added_utc FROM sources WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;
        return new SourceFolder
        {
            Id = r.GetInt64(0),
            Path = r.GetString(1),
            AddedUtc = Parse(r.GetString(2))
        };
    }

    public ImageRecord? GetImage(long id)
    {
        using var cmd = Command("SELECT * FROM images WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRecord(r) : null;
    }

    public ImageRecord? GetImageByPath(string path, bool anyStatus = false)
    {
        var sql = anyStatus
            ? "SELECT * FROM images WHERE path = $p"
            : "SELECT * FROM images WHERE path = $p AND status = 'active'";
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRecord(r) : null;
    }

    private ImageRecord? GetImageByPathInTransaction(string path, bool anyStatus, SqliteTransaction tx)
    {
        var sql = anyStatus
            ? "SELECT * FROM images WHERE path = $p"
            : "SELECT * FROM images WHERE path = $p AND status = 'active'";
        using var cmd = Command(sql);
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRecord(r) : null;
    }

    public List<ImageRecord> GetImages(long? sourceId = null, bool includeMissing = true)
    {
        var sql = "SELECT * FROM images WHERE 1=1";
        using var cmd = Command(sql);
        if (sourceId is not null)
        {
            cmd.CommandText += " AND source_id = $s";
            cmd.Parameters.AddWithValue("$s", sourceId.Value);
        }
        if (!includeMissing)
            cmd.CommandText += " AND status = 'active'";
        cmd.CommandText += " ORDER BY taken_utc IS NULL, taken_utc DESC, path";

        var list = new List<ImageRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadRecord(r));
        return list;
    }

    public int GetImageCount(long? sourceId = null, bool includeMissing = false)
    {
        using var cmd = Command("SELECT COUNT(*) FROM images WHERE ($source IS NULL OR source_id = $source) AND ($missing = 1 OR status = 'active')");
        cmd.Parameters.AddWithValue("source", sourceId is null ? DBNull.Value : sourceId.Value);
        cmd.Parameters.AddWithValue("missing", includeMissing ? 1 : 0);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<ImageRecord> GetMissingImages()
    {
        using var cmd = Command("SELECT * FROM images WHERE status = 'missing' ORDER BY path");
        var list = new List<ImageRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadRecord(r));
        return list;
    }

    public List<ImageRecord> GetImagesByPaths(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return new List<ImageRecord>();

        var list = new List<ImageRecord>();
        var batchSize = 500;
        for (int i = 0; i < pathList.Count; i += batchSize)
        {
            var batch = pathList.Skip(i).Take(batchSize).ToList();
            var placeholders = batch.Select((_, idx) => $"$p{idx}").ToArray();
            var sql = $"SELECT * FROM images WHERE path IN ({string.Join(",", placeholders)}) AND status = 'active'";
            using var cmd = Command(sql);
            for (int j = 0; j < batch.Count; j++)
                cmd.Parameters.AddWithValue(placeholders[j], batch[j]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(ReadRecord(r));
        }
        return list;
    }

    public List<ImageRecord> GetActiveImagesUnderDirectory(string directoryPath)
    {
        var normalized = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = normalized + Path.DirectorySeparatorChar;

        using var cmd = Command("""
            SELECT *
            FROM images
            WHERE status = 'active'
              AND substr(path, 1, length($prefix)) = $prefix COLLATE NOCASE
            ORDER BY COALESCE(taken_utc,file_created_utc,modified_utc) ASC, id ASC
            """);
        cmd.Parameters.AddWithValue("prefix", prefix);

        var list = new List<ImageRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    public List<ImageRecord> GetActiveImagesInDirectory(string directoryPath, bool includeDescendants)
    {
        var records=GetActiveImagesUnderDirectory(directoryPath);
        if(includeDescendants)return records;
        var normalized=Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        return records.Where(record=>string.Equals(Path.GetDirectoryName(Path.GetFullPath(record.Path))?.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),normalized,StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public ImageRecord UpsertImage(ImageRecord rec)
    {
        var existing = GetImageByPath(rec.Path, anyStatus: true);
        if (existing is null)
        {
            using var cmd = Command("""
                INSERT INTO images (source_id, path, file_size, modified_utc, sha256, width, height,
                                    taken_utc, camera, gps, thumb_path, status, indexed_utc, ahash, dhash,file_created_utc)
                VALUES ($sid, $p, $fs, $m, $sha, $w, $h, $t, $cam, $gps, $th, $st, $ix, $ah, $dh,$created);
                SELECT last_insert_rowid();
                """);
            BindRecord(cmd, rec);
            try
            {
                rec.Id = (long)cmd.ExecuteScalar()!;
            }
            catch (SqliteException) when (rec.SourceId > 0)
            {
                // Source not in DB — auto-register based on file parent directory
                rec.SourceId = AddSource(Path.GetDirectoryName(rec.Path)!).Id;
                cmd.Parameters["$sid"].Value = rec.SourceId;
                rec.Id = (long)cmd.ExecuteScalar()!;
            }
            return rec;
        }

        rec.Id = existing.Id;
        UpdateImage(rec);
        return rec;
    }

    public void UpdateImage(ImageRecord rec)
    {
        using var cmd = Command("""
            UPDATE images SET source_id = $sid, path = $p, file_size = $fs, modified_utc = $m,
                sha256 = $sha, width = $w, height = $h, taken_utc = $t, camera = $cam,
                gps = $gps, thumb_path = $th, status = $st, indexed_utc = $ix,
                ahash = $ah, dhash = $dh, file_created_utc=$created
            WHERE id = $id
            """);
        BindRecord(cmd, rec);
        cmd.Parameters.AddWithValue("$id", rec.Id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteImage(long id)
    {
        using var cmd = Command("DELETE FROM images WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkMissing(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0)
            return;
        using var tx = _conn.BeginTransaction();
        foreach (var id in ids)
        {
            using var cmd = Command("UPDATE images SET status = 'missing' WHERE id = $id");
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void BindRecord(SqliteCommand cmd, ImageRecord rec)
    {
        cmd.Parameters.AddWithValue("$sid", rec.SourceId);
        cmd.Parameters.AddWithValue("$p", rec.Path);
        cmd.Parameters.AddWithValue("$fs", rec.FileSize);
        cmd.Parameters.AddWithValue("$m", Fmt(rec.ModifiedUtc));
        cmd.Parameters.AddWithValue("$created", rec.CreatedUtc is null ? DBNull.Value : Fmt(rec.CreatedUtc.Value));
        cmd.Parameters.AddWithValue("$sha", (object?)rec.Sha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", rec.Width);
        cmd.Parameters.AddWithValue("$h", rec.Height);
        cmd.Parameters.AddWithValue("$t", rec.TakenUtc is null ? DBNull.Value : Fmt(rec.TakenUtc.Value));
        cmd.Parameters.AddWithValue("$cam", (object?)rec.Camera ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gps", (object?)rec.Gps ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$th", (object?)rec.ThumbPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$st", rec.Status == ImageStatus.Active ? "active" : "missing");
        cmd.Parameters.AddWithValue("$ix", Fmt(rec.IndexedUtc));
        cmd.Parameters.AddWithValue("$ah", rec.AverageHash);
        cmd.Parameters.AddWithValue("$dh", rec.DifferenceHash);
    }

    private static ImageRecord ReadRecord(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        SourceId = r.GetInt64(r.GetOrdinal("source_id")),
        Path = r.GetString(r.GetOrdinal("path")),
        FileSize = r.GetInt64(r.GetOrdinal("file_size")),
        ModifiedUtc = Parse(r.GetString(r.GetOrdinal("modified_utc"))),
        CreatedUtc = r.IsDBNull(r.GetOrdinal("file_created_utc")) ? null : Parse(r.GetString(r.GetOrdinal("file_created_utc"))),
        Sha256 = r.IsDBNull(r.GetOrdinal("sha256")) ? null : r.GetString(r.GetOrdinal("sha256")),
        Width = r.GetInt32(r.GetOrdinal("width")),
        Height = r.GetInt32(r.GetOrdinal("height")),
        TakenUtc = r.IsDBNull(r.GetOrdinal("taken_utc")) ? null : Parse(r.GetString(r.GetOrdinal("taken_utc"))),
        Camera = r.IsDBNull(r.GetOrdinal("camera")) ? null : r.GetString(r.GetOrdinal("camera")),
        Gps = r.IsDBNull(r.GetOrdinal("gps")) ? null : r.GetString(r.GetOrdinal("gps")),
        ThumbPath = r.IsDBNull(r.GetOrdinal("thumb_path")) ? null : r.GetString(r.GetOrdinal("thumb_path")),
        Status = r.GetString(r.GetOrdinal("status")) == "active" ? ImageStatus.Active : ImageStatus.Missing,
        IndexedUtc = Parse(r.GetString(r.GetOrdinal("indexed_utc"))),
        AverageHash = r.GetInt64(r.GetOrdinal("ahash")),
        DifferenceHash = r.GetInt64(r.GetOrdinal("dhash"))
    };

    public SqliteTransaction BeginTransaction() => _conn.BeginTransaction();

    /// <summary>Creates an independent connection for cancellable background reads.</summary>
    public SqliteConnection OpenReadConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        RegisterFunctions(connection);
        return connection;
    }

    private static void RegisterFunctions(SqliteConnection connection)
    {
        connection.CreateFunction<string?, double, double, double, double, int>(
            "gps_in_bounds",
            (gps, minLat, maxLat, minLon, maxLon) => GeoPlaceResolver.IsWithin(gps, minLat, maxLat, minLon, maxLon) ? 1 : 0);
        connection.CreateFunction<string?, string, int>("gps_in_area",
            (gps, areaKey) => GeoPlaceResolver.IsWithinArea(gps, areaKey) ? 1 : 0);
    }

    public void UpsertImageInTransaction(ImageRecord rec, SqliteTransaction tx)
    {
        var existing = GetImageByPathInTransaction(rec.Path, anyStatus: true, tx);
        if (existing is null)
        {
            using var cmd = Command("""
                INSERT INTO images (source_id, path, file_size, modified_utc, sha256, width, height,
                                    taken_utc, camera, gps, thumb_path, status, indexed_utc, ahash, dhash,file_created_utc)
                VALUES ($sid, $p, $fs, $m, $sha, $w, $h, $t, $cam, $gps, $th, $st, $ix, $ah, $dh,$created);
                SELECT last_insert_rowid();
                """);
            cmd.Transaction = tx;
            BindRecord(cmd, rec);
            try
            {
                rec.Id = (long)cmd.ExecuteScalar()!;
            }
            catch (SqliteException) when (rec.SourceId > 0)
            {
                rec.SourceId = AddSource(Path.GetDirectoryName(rec.Path)!).Id;
                cmd.Parameters["$sid"].Value = rec.SourceId;
                rec.Id = (long)cmd.ExecuteScalar()!;
            }
            return;
        }

        rec.Id = existing.Id;
        UpdateImageInTransaction(rec, tx);
    }

    public void UpdateImageInTransaction(ImageRecord rec, SqliteTransaction tx)
    {
        using var cmd = Command("""
            UPDATE images SET source_id = $sid, path = $p, file_size = $fs, modified_utc = $m,
                sha256 = $sha, width = $w, height = $h, taken_utc = $t, camera = $cam,
                gps = $gps, thumb_path = $th, status = $st, indexed_utc = $ix,
                ahash = $ah, dhash = $dh, file_created_utc=$created
            WHERE id = $id
            """);
        cmd.Transaction = tx;
        BindRecord(cmd, rec);
        cmd.Parameters.AddWithValue("$id", rec.Id);
        cmd.ExecuteNonQuery();
    }

    public void BackupTo(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Pooling = false }.ToString());
        destination.Open();
        _conn.BackupDatabase(destination);
    }

    public void Dispose() => _conn.Dispose();
}
