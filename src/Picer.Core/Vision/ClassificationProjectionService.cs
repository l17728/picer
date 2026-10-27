using System.Security.Cryptography;
using System.Text;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.SemanticSearch;

namespace Picer.Core.Vision;

public sealed record ClassificationRunProgress(string RunId, string Status, int Total, int Processed,
    int Failed, long CursorImageId, string? LastErrorCode);

public sealed record ClassificationReviewItem(long ImageId, string Path, string? ThumbnailPath,
    string ConceptId, string DisplayName, string SourceKind, string AssertionState, string ReviewState,
    int ReferenceCount, DateTime UpdatedUtc);

public sealed class ClassificationProjectionService(PicerDatabase database)
{
    public const string MetadataRuleVersion = "metadata-facts-v1";
    public const string ExistingDescriptionVersion = "existing-description-v1";

    public ClassificationRunProgress BeginOrResume()
    {
        using var latest = database.Command("""
            SELECT run_id,status,cursor_image_id,discovered_count,processed_count,failed_count,last_error_code
            FROM classification_runs WHERE status IN ('paused','cancelled','interrupted','partial_failed')
            ORDER BY updated_utc DESC LIMIT 1
            """);
        using var reader = latest.ExecuteReader();
        if (reader.Read())
        {
            var progress = ReadProgress(reader);
            reader.Close();
            using var resume = database.Command("""
                UPDATE classification_runs SET status='running',updated_utc=$now,finished_utc=NULL,
                    cursor_image_id=CASE WHEN status='partial_failed' THEN 0 ELSE cursor_image_id END,
                    processed_count=CASE WHEN status='partial_failed' THEN 0 ELSE processed_count END,
                    failed_count=CASE WHEN status='partial_failed' THEN 0 ELSE failed_count END,
                    last_error_code=CASE WHEN status='partial_failed' THEN NULL ELSE last_error_code END
                WHERE run_id=$id
                """);
            resume.Parameters.AddWithValue("$id", progress.RunId);
            resume.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
            resume.ExecuteNonQuery();
            return progress.Status == "partial_failed"
                ? progress with { Status = "running", CursorImageId = 0, Processed = 0, Failed = 0, LastErrorCode = null }
                : progress with { Status = "running" };
        }
        var id = Guid.NewGuid().ToString("N");
        var total = CountActiveImages();
        using var insert = database.Command("""
            INSERT INTO classification_runs(run_id,status,cursor_image_id,discovered_count,processed_count,failed_count,started_utc,updated_utc)
            VALUES($id,'running',0,$total,0,0,$now,$now)
            """);
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$total", total);
        insert.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
        insert.ExecuteNonQuery();
        return new(id, "running", total, 0, 0, 0, null);
    }

    public ClassificationRunProgress ProcessNextBatch(string runId, int batchSize = 40,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<long, SemanticAnalysisResult>? semanticResults = null)
    {
        if (batchSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var current = GetRun(runId) ?? throw new InvalidOperationException("classification_run_not_found");
        if (current.Status != "running") return current;
        var images = LoadBatch(current.CursorImageId, batchSize);
        if (images.Count == 0)
        {
            SetStatus(runId, current.Failed == 0 ? "completed" : "partial_failed", finished: true);
            return GetRun(runId)!;
        }

        var inputs = images.Select(image => (Image: image, Suggestions: LoadDescriptionConcepts(image.Id))).ToArray();
        var processed = 0;
        var failed = 0;
        string? lastError = null;
        using var transaction = database.BeginTransaction();
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ProjectMetadata(input.Image, transaction);
                ProjectSuggestions(input.Image, input.Suggestions, transaction);
                if (semanticResults?.TryGetValue(input.Image.Id, out var semantic) == true)
                    ProjectSemanticConcepts(input.Image, semantic, transaction);
                database.ReplayPhotoClassificationFields(input.Image.Id,transaction);
                processed++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failed++;
                lastError = "classification_item_failed";
            }
        }
        using var update = database.Command("""
            UPDATE classification_runs SET cursor_image_id=$cursor,processed_count=processed_count+$processed,
                failed_count=failed_count+$failed,last_error_code=$error,updated_utc=$now WHERE run_id=$id
            """);
        update.Transaction = transaction;
        update.Parameters.AddWithValue("$cursor", images[^1].Id);
        update.Parameters.AddWithValue("$processed", processed);
        update.Parameters.AddWithValue("$failed", failed);
        update.Parameters.AddWithValue("$error", (object?)lastError ?? DBNull.Value);
        update.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
        update.Parameters.AddWithValue("$id", runId);
        update.ExecuteNonQuery();
        transaction.Commit();
        if (semanticResults is not null)
        {
            var embeddings = new ClipSemanticRepository(database);
            foreach (var image in images)
                if (semanticResults.TryGetValue(image.Id, out var semantic) && semantic.Embedding is { Length: > 0 } vector &&
                    !string.IsNullOrWhiteSpace(semantic.EmbeddingModelVersion))
                    embeddings.Upsert(image, semantic.EmbeddingModelVersion, vector);
        }
        return GetRun(runId)!;
    }

    public IReadOnlyList<ImageRecord> GetNextBatch(string runId, int batchSize)
    {
        if (batchSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var current = GetRun(runId) ?? throw new InvalidOperationException("classification_run_not_found");
        return current.Status == "running" ? LoadBatch(current.CursorImageId, batchSize) : [];
    }

    public void Pause(string runId) => SetStatus(runId, "paused", finished: false);
    public void Cancel(string runId) => SetStatus(runId, "cancelled", finished: true);

    public void InterruptAbandonedRuns()
    {
        using var command = database.Command("UPDATE classification_runs SET status='interrupted',updated_utc=$now,finished_utc=$now WHERE status='running'");
        command.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    /// <summary>Retracts obsolete active screenshot labels and reprojects available metadata and description labels.</summary>
    public int RepairObsoleteScreenshotClassifications(CancellationToken cancellationToken = default)
    {
        using var find = database.Command("""
            SELECT DISTINCT i.id FROM images i
            JOIN image_classification_assertions a ON a.image_id=i.id
            WHERE i.status='active' AND a.concept_id='document.screenshot'
              AND a.review_state IN ('pending','accepted')
            ORDER BY i.id
            """);
        using var reader = find.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        reader.Close();

        var repaired = 0;
        foreach (var batch in ids.Chunk(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var images = batch.Select(database.GetImage).Where(image => image is not null).Cast<ImageRecord>().ToArray();
            using var transaction = database.BeginTransaction();
            foreach (var image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProjectMetadata(image, transaction);
                ProjectSuggestions(image, LoadDescriptionConcepts(image.Id), transaction);
                using (var removeLegacy = database.Command("DELETE FROM image_visual_concepts WHERE image_id=$image AND concept_id='document.screenshot'"))
                {
                    removeLegacy.Transaction = transaction;
                    removeLegacy.Parameters.AddWithValue("$image", image.Id);
                    removeLegacy.ExecuteNonQuery();
                }
                using var retract = database.Command("""
                    UPDATE image_classification_assertions SET review_state='retracted',updated_utc=$now
                    WHERE image_id=$image AND concept_id='document.screenshot'
                      AND review_state IN ('pending','accepted')
                    """);
                retract.Transaction = transaction;
                retract.Parameters.AddWithValue("$image", image.Id);
                retract.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
                retract.ExecuteNonQuery();
                repaired++;
            }
            transaction.Commit();
        }
        return repaired;
    }

    public ClassificationRunProgress? GetLatestRun()
    {
        using var command = database.Command("""
            SELECT run_id,status,cursor_image_id,discovered_count,processed_count,failed_count,last_error_code
            FROM classification_runs ORDER BY updated_utc DESC LIMIT 1
            """);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProgress(reader) : null;
    }

    public IReadOnlyList<ClassificationReviewItem> List(string view, int limit = 500)
    {
        if (string.Equals(view, "people", StringComparison.Ordinal)) return ListPeople(limit);
        if (string.Equals(view, "ownership", StringComparison.Ordinal)) return ListUnresolvedOwnership(limit);
        var predicate = view switch
        {
            "automatic" => "a.source_kind='vision_model' AND a.assertion_state='automatic' AND a.review_state IN ('pending','accepted')",
            "confirmed" => "a.assertion_state='user_confirmed' AND a.review_state='accepted'",
            "ignored" => "a.source_kind='vision_model' AND a.review_state='rejected'",
            _ => "a.assertion_state='suggested' AND a.review_state='pending'"
        };
        using var command = database.Command($"""
            SELECT a.image_id,i.path,i.thumb_path,a.concept_id,a.source_kind,a.assertion_state,a.review_state,
                   CASE WHEN a.content_sha256 IS NULL THEN 1 ELSE
                       (SELECT COUNT(*) FROM images r WHERE r.status='active' AND lower(COALESCE(r.content_sha256,r.sha256))=a.content_sha256
                           AND (SELECT owner_key FROM sync_image_owners WHERE image_id=r.id)=
                               (SELECT owner_key FROM sync_image_owners WHERE image_id=i.id)) END,
                   a.updated_utc,c.display_name
            FROM image_classification_assertions a JOIN images i ON i.id=a.image_id
            LEFT JOIN semantic_content_clusters c ON a.concept_id='cluster.semantic.' || c.cluster_key
            WHERE i.status='active' AND {predicate}
              {(view == "ignored" ? "" : "AND (a.source_kind<>'vision_model' OR NOT EXISTS(SELECT 1 FROM image_classification_rejections r WHERE r.concept_id=a.concept_id AND r.source_scope IN ('vision_model','all') AND (r.image_id=i.id OR (r.content_sha256=a.content_sha256 AND r.owner_key=(SELECT owner_key FROM sync_image_owners WHERE image_id=i.id)))))")}
            ORDER BY a.updated_utc DESC,a.id DESC LIMIT $limit
            """);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var items = new List<ClassificationReviewItem>();
        while (reader.Read())
        {
            var concept = reader.GetString(3);
            items.Add(new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                concept, reader.IsDBNull(9) ? VisualConceptCatalog.DisplayName(concept) : reader.GetString(9), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetInt32(7), DateTime.Parse(reader.GetString(8), null,
                    System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return items;
    }

    private IReadOnlyList<ClassificationReviewItem> ListPeople(int limit)
    {
        using var command = database.Command("""
            SELECT DISTINCT i.id,i.path,i.thumb_path,p.id,p.display_name,a.updated_utc
            FROM detected_faces f
            JOIN images i ON i.id=f.image_id AND i.status='active'
            JOIN face_person_assignments a ON a.face_id=f.id AND a.state='confirmed'
            JOIN people p ON p.id=a.person_id
            WHERE f.is_archived=0
            ORDER BY a.updated_utc DESC,i.id DESC LIMIT $limit
            """);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader(); var items = new List<ClassificationReviewItem>();
        while (reader.Read()) items.Add(new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            "person." + reader.GetInt64(3), reader.GetString(4), "person_metadata", "user_confirmed", "accepted", 1,
            DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return items;
    }

    public IReadOnlyList<ImageRecord> ListUnclassified(int limit = 500)
    {
        using var command = database.Command("""
            SELECT id FROM images i WHERE status='active' AND NOT EXISTS(
                SELECT 1 FROM image_classification_assertions a WHERE a.image_id=i.id
                  AND a.review_state IN ('pending','accepted') AND a.assertion_state IN ('suggested','automatic','user_confirmed','legacy_effective'))
            ORDER BY indexed_utc DESC,id DESC LIMIT $limit
            """);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var ids = new List<long>(); while (reader.Read()) ids.Add(reader.GetInt64(0)); reader.Close();
        return ids.Select(database.GetImage).Where(image => image is not null).Cast<ImageRecord>().ToArray();
    }

    private IReadOnlyList<ClassificationReviewItem> ListUnresolvedOwnership(int limit)
    {
        using var command = database.Command("""
            WITH candidates AS (
                SELECT MIN(i.id) AS image_id,r.concept_id,MAX(r.rejected_utc) AS rejected_utc,COUNT(DISTINCT i.id) AS references_count
                FROM image_classification_rejections r
                JOIN images i ON lower(COALESCE(i.content_sha256,i.sha256))=lower(r.content_sha256)
                JOIN sync_image_owners o ON o.image_id=i.id
                WHERE r.owner_key='' AND i.status='active'
                  AND NOT EXISTS(SELECT 1 FROM image_classification_rejections known
                      WHERE known.owner_key=o.owner_key AND known.concept_id=r.concept_id
                        AND lower(known.content_sha256)=lower(r.content_sha256) AND known.source_scope=r.source_scope)
                  AND NOT EXISTS(SELECT 1 FROM classification_rejection_ownership_reviews v
                      WHERE v.content_sha256=lower(r.content_sha256) AND v.concept_id=r.concept_id AND v.owner_key=o.owner_key)
                GROUP BY lower(r.content_sha256),r.concept_id,o.owner_key
            )
            SELECT i.id,i.path,i.thumb_path,c.concept_id,c.rejected_utc,c.references_count
            FROM candidates c JOIN images i ON i.id=c.image_id ORDER BY c.rejected_utc DESC,i.id LIMIT $limit
            """);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var result = new List<ClassificationReviewItem>();
        while (reader.Read())
            result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3), VisualConceptCatalog.DisplayName(reader.GetString(3)), "ownership_review", "legacy_effective",
                "pending", reader.GetInt32(5), DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return result;
    }

    public void RestoreSuggestion(long imageId, string conceptId)
    {
        using var transaction=database.BeginTransaction();
        ClassificationOwnershipReview.Record(database,transaction,imageId,conceptId,"restored");
        database.RecordPhotoClassificationDecision(imageId,conceptId,"restored",false,transaction);
        transaction.Commit();
    }

    private void ProjectMetadata(ImageRecord image, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        var facts = new List<(string Concept, string Evidence)>();
        if (image.Width > 0 && image.Height > 0)
            facts.Add((image.Width == image.Height ? "metadata.orientation.square" : image.Width > image.Height
                ? "metadata.orientation.landscape" : "metadata.orientation.portrait", $"{image.Width}x{image.Height}"));
        if (!string.IsNullOrWhiteSpace(image.Camera)) facts.Add(("metadata.camera.present", "camera-field-present"));
        if (!string.IsNullOrWhiteSpace(image.Gps)) facts.Add(("metadata.gps.present", "gps-field-present"));
        using (var retract = database.Command("""
            UPDATE image_classification_assertions SET review_state='retracted',updated_utc=$now
            WHERE image_id=$image AND source_kind='metadata_rule' AND source_version=$version
            """))
        {
            retract.Transaction = transaction; retract.Parameters.AddWithValue("$image", image.Id);
            retract.Parameters.AddWithValue("$version", MetadataRuleVersion); retract.Parameters.AddWithValue("$now", Format(DateTime.UtcNow)); retract.ExecuteNonQuery();
        }
        foreach (var fact in facts) UpsertAssertion(image, fact.Concept, "metadata_rule", MetadataRuleVersion,
            "automatic", "accepted", fact.Evidence, transaction);
    }

    private void ProjectSuggestions(ImageRecord image, IReadOnlyList<(string Concept, string Version, string Evidence)> suggestions,
        Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        foreach (var suggestion in suggestions)
            UpsertAssertion(image, suggestion.Concept, "vision_model", suggestion.Version, "suggested", "pending",
                suggestion.Evidence, transaction, respectRejection: true);
    }

    private void ProjectSemanticConcepts(ImageRecord image, SemanticAnalysisResult result,
        Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        const string version = "chinese-clip-zero-shot-v1";
        using (var retract = database.Command("UPDATE image_classification_assertions SET review_state='retracted',updated_utc=$now WHERE image_id=$image AND source_kind='vision_model' AND source_version=$version"))
        {
            retract.Transaction = transaction;
            retract.Parameters.AddWithValue("$image", image.Id);
            retract.Parameters.AddWithValue("$version", version);
            retract.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
            retract.ExecuteNonQuery();
        }
        foreach (var item in result.Concepts.DistinctBy(value => value.ConceptId))
            UpsertAssertion(image, item.ConceptId, "vision_model", version, "automatic", "accepted",
                $"clip:{item.Confidence:F4}", transaction, respectRejection: true, confidence: item.Confidence);
    }

    private void UpsertAssertion(ImageRecord image, string concept, string source, string version, string state,
        string review, string evidence, Microsoft.Data.Sqlite.SqliteTransaction transaction, bool respectRejection = false,
        float? confidence = null)
    {
        // Only the current verified digest can share decisions across references.
        // ContentSha256 may still describe the previous bytes during reindexing.
        var hash = NormalizeHash(image.Sha256);
        using var command = database.Command($"""
            INSERT INTO image_classification_assertions(image_id,content_sha256,concept_id,source_kind,source_version,
                assertion_state,review_state,confidence,evidence_ref,evidence_fingerprint,created_utc,updated_utc)
            SELECT $image,$sha,$concept,$source,$version,$state,$review,$confidence,$evidence,$fingerprint,$now,$now
            {(respectRejection ? "WHERE NOT EXISTS (SELECT 1 FROM image_classification_rejections r WHERE r.concept_id=$concept AND r.source_scope IN ('vision_model','all') AND (r.image_id=$image OR ($sha IS NOT NULL AND r.content_sha256=$sha AND r.owner_key=(SELECT owner_key FROM sync_image_owners WHERE image_id=$image))))" : "")}
            ON CONFLICT(image_id,concept_id,source_kind,source_version) DO UPDATE SET
                content_sha256=excluded.content_sha256,assertion_state=excluded.assertion_state,
                review_state=CASE WHEN image_classification_assertions.review_state IN ('rejected','accepted') THEN image_classification_assertions.review_state ELSE excluded.review_state END,
                confidence=excluded.confidence,evidence_ref=excluded.evidence_ref,evidence_fingerprint=excluded.evidence_fingerprint,updated_utc=excluded.updated_utc
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$image", image.Id); command.Parameters.AddWithValue("$sha", (object?)hash ?? DBNull.Value);
        command.Parameters.AddWithValue("$concept", concept); command.Parameters.AddWithValue("$source", source); command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$state", state); command.Parameters.AddWithValue("$review", review); command.Parameters.AddWithValue("$evidence", evidence);
        command.Parameters.AddWithValue("$confidence", (object?)confidence ?? DBNull.Value);
        command.Parameters.AddWithValue("$fingerprint", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))).ToLowerInvariant());
        command.Parameters.AddWithValue("$now", Format(DateTime.UtcNow)); command.ExecuteNonQuery();
    }

    private IReadOnlyList<(string Concept, string Version, string Evidence)> LoadDescriptionConcepts(long imageId)
    {
        var latest = new CloudVisionRepository(database).GetLatestCompleted(imageId);
        if (latest is null) return [];
        var version = $"{ExistingDescriptionVersion}:{latest.Value.Provider}:{latest.Value.ModelVersion}:{latest.Value.PromptVersion}";
        var result = latest.Value.Result;
        return (result.Scenes ?? []).Concat(result.Activities ?? []).Concat(result.Objects ?? [])
            .Concat(result.SearchTerms ?? []).Append(result.Summary ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => ResolveConcepts(value).Select(concept =>
                (Concept: concept, Version: version, Evidence: value.Trim())))
            .DistinctBy(item => item.Concept).ToArray();
    }

    private static IReadOnlyList<string> ResolveConcepts(string value)
    {
        if (VisualConceptCatalog.TryResolve(value, out var exact)) return [exact];
        return VisualConceptCatalog.Parse(value).ConceptIds;
    }

    private IReadOnlyList<ImageRecord> LoadBatch(long cursor, int count)
    {
        using var command = database.Command("SELECT id FROM images WHERE status='active' AND id>$cursor ORDER BY id LIMIT $count");
        command.Parameters.AddWithValue("$cursor", cursor); command.Parameters.AddWithValue("$count", count);
        using var reader = command.ExecuteReader(); var ids = new List<long>(); while (reader.Read()) ids.Add(reader.GetInt64(0)); reader.Close();
        return ids.Select(database.GetImage).Where(image => image is not null).Cast<ImageRecord>().ToArray();
    }

    private int CountActiveImages() { using var command = database.Command("SELECT COUNT(*) FROM images WHERE status='active'"); return Convert.ToInt32(command.ExecuteScalar()); }
    private ClassificationRunProgress? GetRun(string id)
    {
        using var command = database.Command("SELECT run_id,status,cursor_image_id,discovered_count,processed_count,failed_count,last_error_code FROM classification_runs WHERE run_id=$id");
        command.Parameters.AddWithValue("$id", id); using var reader = command.ExecuteReader(); return reader.Read() ? ReadProgress(reader) : null;
    }
    private static ClassificationRunProgress ReadProgress(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(2), reader.IsDBNull(6) ? null : reader.GetString(6));
    private void SetStatus(string id, string status, bool finished)
    {
        using var command = database.Command("UPDATE classification_runs SET status=$status,updated_utc=$now,finished_utc=CASE WHEN $finished=1 THEN $now ELSE NULL END WHERE run_id=$id");
        command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$now", Format(DateTime.UtcNow)); command.Parameters.AddWithValue("$finished", finished ? 1 : 0); command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }
    private static string? NormalizeHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    private static string Format(DateTime value) => value.ToUniversalTime().ToString("O");
}
