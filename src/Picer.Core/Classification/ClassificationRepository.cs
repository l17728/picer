using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.Classification;

/// <summary>One connection per operation. Read methods never seed, repair, or publish data.</summary>
public sealed partial class ClassificationRepository(PicerDatabase database)
{
    private SqliteTransaction? _transaction;
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    internal static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new InvalidDataException("classification_payload_invalid");
    internal static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string Now => DateTimeOffset.UtcNow.ToString("O");
    private SqliteCommand Sql(string sql, params (string Key, object? Value)[] parameters)
    {
        var command = database.Command(sql);
        command.Transaction = _transaction;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private int Execute(string sql, params (string Key, object? Value)[] parameters)
    { using var command = Sql(sql, parameters); return command.ExecuteNonQuery(); }
    private string? Scalar(string sql, params (string Key, object? Value)[] parameters)
    { using var command = Sql(sql, parameters); return command.ExecuteScalar()?.ToString(); }
    private T Write<T>(Func<T> action)
    {
        if (_transaction is not null) return action();
        using var transaction = database.BeginTransaction();
        _transaction = transaction;
        try { var value = action(); transaction.Commit(); return value; }
        finally { _transaction = null; }
    }
    public string Epoch => Scalar("SELECT epoch FROM mirror_revision WHERE id=1") ?? throw new InvalidDataException("classification_epoch_missing");

    public ClassificationTaxonomySnapshot Taxonomy()
    {
        var preset = ClassificationPreset.Load();
        var payload = Scalar("SELECT payload FROM classification_v2_taxonomy WHERE id=1");
        if (payload is not null) return preset.Upgrade(Decode<ClassificationTaxonomySnapshot>(payload));
        return new(1, 1, preset.Labels, preset.Edges, preset.Labels.Where(l => l.Assignable).Select(l => new ClassificationDefinitionVersion(l.Id, 1)).ToArray());
    }

    public ClassificationConfiguration? Configuration()
    {
        var payload = Scalar("SELECT payload FROM classification_v2_settings WHERE id=1");
        return payload is null ? null : Decode<ClassificationConfiguration>(payload);
    }

    public void Configure(ClassificationConfiguration configuration)
    {
        ValidateConfiguration(configuration);
        Execute("INSERT INTO classification_v2_settings VALUES(1,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload", ("$payload", Encode(configuration)));
    }

    public static void ValidateConfiguration(ClassificationConfiguration configuration)
    {
        if (configuration.MaximumCalls is < 1 or > 10000 || configuration.MaximumOutputTokens is < 1024 or > 32768
            || string.IsNullOrWhiteSpace(configuration.Model) || configuration.Model.Length > 256
            || string.IsNullOrWhiteSpace(configuration.PromptVersion) || string.IsNullOrWhiteSpace(configuration.PreprocessingVersion))
            throw new ArgumentException("classification_config_invalid");
        // Endpoint is an opaque routing identity, never a URL fetched by the index service.
        if (configuration.Endpoint is not ("local" or "configured-agent")) throw new ArgumentException("classification_endpoint_invalid");
        if (configuration.Enabled && configuration.Endpoint != "local" && !configuration.AllowRemote)
            throw new ArgumentException("classification_remote_not_authorized");
    }

    public IReadOnlyList<ClassificationContext> Contexts(ClassificationConfiguration configuration, long? onlyImage = null,
        long afterImageId = 0, int limit = 0, IReadOnlyCollection<long>? imageIds = null)
    {
        if (afterImageId < 0 || limit < 0 || onlyImage is not null && imageIds is not null)
            throw new ArgumentOutOfRangeException(nameof(afterImageId));
        var taxonomy = Taxonomy();
        var epoch = Epoch;
        var idsJson = imageIds is null ? null : JsonSerializer.Serialize(imageIds);
        var images = new List<(ImageRecord Image, string Owner, string Identity)>();
        using (var command = Sql("""
            SELECT i.id,i.source_id,i.path,i.file_size,i.modified_utc,i.sha256,i.width,i.height,i.taken_utc,i.camera,i.gps,i.thumb_path,
                   COALESCE(o.owner_key,'local-source:'||i.source_id),
                   CASE WHEN length(i.sha256)=64 AND lower(i.sha256) NOT GLOB '*[^0-9a-f]*' THEN 'sha256:'||lower(i.sha256)
                     ELSE 'local:'||$epoch||':'||COALESCE(v.version_id,CAST(i.id AS TEXT)||':'||i.file_size||':'||i.modified_utc) END
            FROM images i LEFT JOIN sync_image_owners o ON o.image_id=i.id
            LEFT JOIN photo_metadata_file_versions v ON v.image_id=i.id
            WHERE i.status='active' AND ($id IS NULL OR i.id=$id)
              AND ($ids IS NULL OR EXISTS(SELECT 1 FROM json_each($ids) selected WHERE CAST(selected.value AS INTEGER)=i.id))
              AND ($after=0 OR i.id>$after)
            ORDER BY i.id LIMIT CASE WHEN $limit=0 THEN -1 ELSE $limit END
            """, ("$epoch", epoch), ("$id", onlyImage), ("$ids", idsJson), ("$after", afterImageId), ("$limit", limit)))
        using (var r = command.ExecuteReader())
            while (r.Read()) images.Add((new ImageRecord { Id = r.GetInt64(0), SourceId = r.GetInt64(1), Path = r.GetString(2),
                FileSize = r.GetInt64(3), ModifiedUtc = DateTime.Parse(r.GetString(4), null, DateTimeStyles.RoundtripKind),
                Sha256 = r.IsDBNull(5) ? null : r.GetString(5), Width = r.GetInt32(6), Height = r.GetInt32(7),
                TakenUtc = r.IsDBNull(8) ? null : DateTime.Parse(r.GetString(8), null, DateTimeStyles.RoundtripKind),
                Camera = r.IsDBNull(9) ? null : r.GetString(9), Gps = r.IsDBNull(10) ? null : r.GetString(10),
                ThumbPath = r.IsDBNull(11) ? null : r.GetString(11) }, r.GetString(12), r.GetString(13)));
        var throughImageId = images.Count == 0 ? 0 : images[^1].Image.Id;
        var descriptions = new Dictionary<long, Dictionary<string, string>>();
        using (var command = Sql("""
            SELECT a.image_id,a.provider,a.model_version,a.prompt_version,a.result_json,a.source_file_size,a.source_modified_utc
            FROM cloud_vision_analyses a JOIN images i ON i.id=a.image_id
            WHERE i.status='active' AND a.status='completed' AND a.result_json IS NOT NULL
              AND a.source_file_size=i.file_size AND picer_same_instant(a.source_modified_utc,i.modified_utc)
              AND ($id IS NULL OR i.id=$id)
              AND ($ids IS NULL OR EXISTS(SELECT 1 FROM json_each($ids) selected WHERE CAST(selected.value AS INTEGER)=a.image_id))
              AND ($after=0 OR a.image_id>$after)
              AND ($limit=0 OR a.image_id <= $through)
            ORDER BY a.image_id,a.provider
            """, ("$id", onlyImage), ("$ids", idsJson), ("$after", afterImageId), ("$limit", limit), ("$through", throughImageId)))
        using (var r = command.ExecuteReader())
            while (r.Read())
            {
                if (!descriptions.TryGetValue(r.GetInt64(0), out var values)) descriptions[r.GetInt64(0)] = values = new(StringComparer.Ordinal);
                // Current provider projections only; archived/corrected versions are not supporting evidence.
                values["description:" + Digest(r.GetString(1) + "\n" + r.GetString(2) + "\n" + r.GetString(3))] = r.GetString(4);
            }
        var people = new Dictionary<long, List<string>>();
        using (var command = Sql("""
            SELECT f.image_id,a.person_id,a.face_id,a.updated_utc FROM detected_faces f
            JOIN face_person_assignments a ON a.face_id=f.id AND a.state='confirmed'
            WHERE f.is_archived=0 AND ($id IS NULL OR f.image_id=$id)
              AND ($ids IS NULL OR EXISTS(SELECT 1 FROM json_each($ids) selected WHERE CAST(selected.value AS INTEGER)=f.image_id))
              AND ($after=0 OR f.image_id>$after)
              AND ($limit=0 OR f.image_id <= $through)
            ORDER BY f.image_id,a.face_id
            """, ("$id", onlyImage), ("$ids", idsJson), ("$after", afterImageId), ("$limit", limit), ("$through", throughImageId)))
        using (var r = command.ExecuteReader())
            while (r.Read())
            {
                if (!people.TryGetValue(r.GetInt64(0), out var values)) people[r.GetInt64(0)] = values = [];
                values.Add(Encode(new { Person = r.GetInt64(1), Face = r.GetInt64(2), Version = r.GetString(3) }));
            }
        return images.Select(row =>
        {
            var evidence = descriptions.GetValueOrDefault(row.Image.Id) ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var descriptionEvidence = evidence.Select(e => new ClassificationEvidence(e.Key, Digest(e.Value), e.Key)).ToArray();
            var personEvidence = (people.GetValueOrDefault(row.Image.Id) ?? []).Select(p => new ClassificationEvidence("person:" + Digest(p), Digest(p), "confirmed-assignment")).ToArray();
            // No person names/aliases or precise coordinates are sent to the classifier.
            if (personEvidence.Length > 0) evidence["people:confirmed"] = "存在人工确认的人物；不得猜测身份或关系。";
            var metadata = Encode(new { row.Image.TakenUtc, row.Image.Width, row.Image.Height });
            evidence["metadata:current"] = metadata;
            evidence["image:current"] = "本次核验的照片";
            return new ClassificationContext(row.Image, new(epoch, row.Owner, row.Identity, descriptionEvidence,
                personEvidence, [new("metadata:current", Digest(metadata), "index")], configuration.Model + "|" + configuration.Endpoint + "|" + configuration.ModelAgentId,
                configuration.PreprocessingVersion, configuration.PromptVersion, taxonomy.Definitions,
                taxonomy.DiscoveryRevision, "exhaustive-labels-v1"), evidence);
        }).ToArray();
    }

    public ClassificationPlan Plan(string requestId, ClassificationConfiguration configuration, bool retryFailures = false)
    {
        ValidateRequestId(requestId);
        ValidateConfiguration(configuration);
        ClassificationPlan plan;
        using (var read = database.BeginReadTransaction())
        {
            _transaction = read;
            try
            {
            var old = Scalar("SELECT payload FROM classification_v2_plans WHERE request_id=$request", ("$request", requestId));
            if (old is not null) return Decode<ClassificationPlan>(old);
            var successes = new Dictionary<long, string>();
            using (var command = Sql("SELECT image_id,success_fingerprint FROM classification_v2_work WHERE success_fingerprint IS NOT NULL"))
            using (var rows = command.ExecuteReader()) while (rows.Read()) successes[rows.GetInt64(0)] = rows.GetString(1);
            var computeFingerprint = ClassificationInputFingerprint.CreateBatchComputer(Taxonomy().Definitions);
            var failures = new Dictionary<long, (string Fingerprint, string? RetryAfter)>();
            using (var command = Sql("SELECT image_id,attempt_fingerprint,retry_after FROM classification_v2_work WHERE outcome='failed' AND attempt_fingerprint IS NOT NULL"))
            using (var rows = command.ExecuteReader()) while (rows.Read()) failures[rows.GetInt64(0)] = (rows.GetString(1), rows.IsDBNull(2) ? null : rows.GetString(2));
            var items = new List<ClassificationPlanItem>();
            var deferred = 0;
            var total = 0;
            var unchanged = 0;
            long afterImageId = 0;
            const int planPageSize = 200;
            while (true)
            {
                var contexts = Contexts(configuration, afterImageId: afterImageId, limit: planPageSize);
                if (contexts.Count == 0) break;
                total += contexts.Count;
                foreach (var context in contexts)
                {
                    afterImageId = context.Image.Id;
                    var fingerprint = computeFingerprint(context.Input);
                    if (successes.GetValueOrDefault(context.Image.Id) == fingerprint && !failures.ContainsKey(context.Image.Id))
                    { unchanged++; continue; }
                    var previous = successes.ContainsKey(context.Image.Id)
                        ? Scalar("SELECT success_input FROM classification_v2_work WHERE image_id=$id", ("$id", context.Image.Id)) : null;
                    var decision = ClassificationIncrementalPlanner.Decide(context.Input, previous is null ? null : Decode<ClassificationInput>(previous));
                    if (failures.TryGetValue(context.Image.Id, out var failed) && failed.Fingerprint == decision.DesiredFingerprint)
                    {
                        if (!retryFailures && (failed.RetryAfter is null || DateTimeOffset.Parse(failed.RetryAfter, CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow))
                        { deferred++; continue; }
                        decision = decision with { Action = ClassificationPlanAction.Enqueue, Reasons = decision.Reasons | ClassificationUpdateReason.RetryDue };
                    }
                    if (decision.Action != ClassificationPlanAction.Skip)
                        items.Add(new(context.Image.Id, decision.DesiredFingerprint, null, decision.Reasons));
                    else unchanged++;
                }
            }
            plan = new ClassificationPlan(Guid.NewGuid().ToString("N"), requestId, Epoch, Taxonomy().Revision,
                DateTimeOffset.UtcNow.AddMinutes(30), configuration, total, unchanged, items,
                long.Parse(Scalar("SELECT input_revision FROM classification_v2_revision WHERE id=1")!, CultureInfo.InvariantCulture), deferred);
            }
            finally { _transaction = null; }
        }
        return Write(() =>
        {
            var existing = Scalar("SELECT payload FROM classification_v2_plans WHERE request_id=$request", ("$request", requestId));
            if (existing is not null) return Decode<ClassificationPlan>(existing);
            Execute("INSERT INTO classification_v2_plans VALUES($id,$request,$now,$payload)", ("$id", plan.Id), ("$request", requestId), ("$now", Now), ("$payload", Encode(plan)));
            return plan;
        });
    }

    internal static void ValidateRequestId(string value)
    { if (!Guid.TryParseExact(value, "N", out _)) throw new ArgumentException("classification_request_id_invalid"); }
}
