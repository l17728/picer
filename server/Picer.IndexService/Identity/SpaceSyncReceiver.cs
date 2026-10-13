using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Video;

namespace Picer.IndexService.Identity;

/// <summary>Short, synchronous SQLite transactions: identity mapping, business data, sequence and receipt commit together.</summary>
public sealed partial class SpaceSyncReceiver(PicerDatabase database, AuthorizedDevice device)
{
    private static readonly IReadOnlyDictionary<string, string> IdentityTables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["image"] = "images", ["video"] = "videos", ["tag"] = "tags", ["album"] = "albums",
        ["person"] = "people", ["face"] = "detected_faces", ["keyframe"] = "video_keyframes",
        ["video-face"] = "video_detected_faces", ["face-cluster"] = "face_candidate_clusters"
    };

    public SpacePublisher Register(SpacePublisherRequest request)
    {
        if (!device.Allows("index.write") || !Guid.TryParseExact(request.LibraryId, "N", out _) || request.IncarnationId != device.DeviceId)
            throw new UnauthorizedAccessException("publisher_registration_denied");
        using var transaction = database.BeginTransaction();
        var epoch = SpaceEpoch();
        using (var find = Sql("SELECT publisher_id,device_grant_id,last_sequence,epoch,state FROM sync_publishers WHERE library_id=$library AND incarnation_id=$incarnation",
            ("$library", request.LibraryId), ("$incarnation", request.IncarnationId)))
        using (var reader = find.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(1) != device.GrantId) throw new UnauthorizedAccessException("publisher_owned_by_other_device");
                if (reader.GetString(4) != "receiving") throw new InvalidOperationException("publisher_epoch_reset_required");
                var result = new SpacePublisher(reader.GetString(0), request.LibraryId, request.IncarnationId, reader.GetString(3), reader.GetInt64(2));
                reader.Close(); transaction.Commit(); return result;
            }
        }
        var id = Guid.NewGuid().ToString("N");
        Execute("INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch) VALUES($id,$library,$incarnation,$grant,$space,'receiving',$epoch)",
            ("$id", id), ("$library", request.LibraryId), ("$incarnation", request.IncarnationId), ("$grant", device.GrantId), ("$space", device.SpaceId), ("$epoch", epoch));
        transaction.Commit();
        return new(id, request.LibraryId, request.IncarnationId, epoch, 0);
    }

    public SpaceSyncReceipt Apply(SpaceSyncBatch batch)
    {
        if (!device.Allows("index.write") || !Guid.TryParseExact(batch.PublisherId, "N", out _) ||
            !Guid.TryParseExact(batch.OperationId, "N", out _) || batch.Sequence <= 0 || batch.Identities is null || batch.Identities.Count > 4000 || batch.Payload.ValueKind != JsonValueKind.Object)
            throw new UnauthorizedAccessException("invalid_sync_batch");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(batch, DeviceAuthorization.Json)));
        using var transaction = database.BeginTransaction();
        if (SpaceEpoch() != batch.Epoch) throw new InvalidOperationException("space_epoch_reset_required");
        long last;
        using (var publisher = Sql("SELECT last_sequence,epoch FROM sync_publishers WHERE publisher_id=$id AND device_grant_id=$grant AND space_id=$space AND state='receiving'",
            ("$id", batch.PublisherId), ("$grant", device.GrantId), ("$space", device.SpaceId)))
        using (var row = publisher.ExecuteReader())
        {
            if (!row.Read()) throw new UnauthorizedAccessException("publisher_unavailable");
            last = row.GetInt64(0);
            if (row.GetString(1) != batch.Epoch) throw new InvalidOperationException("publisher_epoch_reset_required");
        }
        if (batch.Sequence <= last)
        {
            using var previous = Sql("SELECT operation_id,payload_hash,result FROM sync_operation_receipts WHERE publisher_id=$id AND sequence=$sequence",
                ("$id", batch.PublisherId), ("$sequence", batch.Sequence));
            using var row = previous.ExecuteReader();
            if (!row.Read() || row.GetString(0) != batch.OperationId || row.GetString(1) != hash)
                throw new InvalidOperationException("sync_sequence_conflict");
            return JsonSerializer.Deserialize<SpaceSyncReceipt>(row.GetString(2), DeviceAuthorization.Json)!;
        }
        if (batch.Sequence != last + 1) throw new InvalidOperationException("sync_sequence_gap");
        Execute("SAVEPOINT sync_business");
        SpaceSyncReceipt receipt;
        try
        {
            var identities = BindIdentities(batch.PublisherId, batch.Identities);
            var accepted = batch.Kind switch
            {
                "images" => ApplyImages(batch.PublisherId, batch.Payload.Deserialize<SpaceImageBatch>(DeviceAuthorization.Json) ?? throw new ArgumentException(), identities),
                "sources" => ApplySource(batch.PublisherId, batch.Payload.Deserialize<SpaceSourceBatch>(DeviceAuthorization.Json) ?? throw new ArgumentException()),
                "videos" => ApplyVideos(batch.PublisherId, batch.Payload.Deserialize<SpaceVideoBatch>(DeviceAuthorization.Json) ?? throw new ArgumentException(), identities),
                "catalog" => ApplyCatalog(batch.PublisherId, batch.Payload.Deserialize<SpaceCatalogBatch>(DeviceAuthorization.Json) ?? throw new ArgumentException(), identities),
                "metadata" => ApplyMetadata(batch.PublisherId, batch.Payload.Deserialize<SpaceMetadataBatch>(DeviceAuthorization.Json) ?? throw new ArgumentException(), identities, transaction),
                "metadata-edits" => ApplyMetadataEdits(batch.PublisherId, batch.Payload.Deserialize<SpaceMetadataEditsBatch>(DeviceAuthorization.Json) ?? throw new ArgumentException(), identities, transaction),
                _ => throw new ArgumentException("unsupported_sync_kind")
            };
            Execute("RELEASE sync_business");
            receipt = new(batch.Sequence, batch.OperationId, 200, "accepted", accepted);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidDataException or FormatException or OverflowException || exception is SqliteException { SqliteErrorCode: 19 })
        {
            Execute("ROLLBACK TO sync_business; RELEASE sync_business");
            var code = exception is SqliteException sqlite ? "sync_constraint_" + sqlite.SqliteExtendedErrorCode
                : exception is ArgumentException argument && argument.Message is "invalid_source_id" or "invalid_image_source" or "invalid_video_source" or
                    "missing_global_identity" or "global_identity_changed" or "path_owned_by_another_identity" or "unpublished_reference"
                    ? argument.Message : "invalid_sync_payload";
            receipt = new(batch.Sequence, batch.OperationId, 422, code, 0);
        }
        Execute("INSERT INTO sync_operation_receipts(publisher_id,sequence,operation_id,payload_hash,status,result) VALUES($id,$sequence,$operation,$hash,$status,$result)",
            ("$id", batch.PublisherId), ("$sequence", batch.Sequence), ("$operation", batch.OperationId), ("$hash", hash),
            ("$status", receipt.Status), ("$result", JsonSerializer.Serialize(receipt, DeviceAuthorization.Json)));
        Execute("UPDATE sync_publishers SET last_sequence=$sequence WHERE publisher_id=$id", ("$sequence", batch.Sequence), ("$id", batch.PublisherId));
        Execute("UPDATE sync_space_state SET change_sequence=change_sequence+1 WHERE id=1");
        transaction.Commit();
        return receipt;
    }

    private Dictionary<(string, long), long> BindIdentities(string publisher, IReadOnlyList<SpaceEntityIdentity> identities)
    {
        var result = new Dictionary<(string, long), long>();
        foreach (var identity in identities)
        {
            if (identity is null) throw new ArgumentException("invalid_sync_identity");
            if (!IdentityTables.TryGetValue(identity.Kind, out var table) || identity.LocalId <= 0 ||
                !Guid.TryParseExact(identity.GlobalId, "N", out _) || result.ContainsKey((identity.Kind, identity.LocalId)))
                throw new ArgumentException("invalid_sync_identity");
            using (var existing = Sql("SELECT global_id,target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND local_key=$local",
                ("$publisher", publisher), ("$kind", identity.Kind), ("$local", identity.LocalId.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            using (var row = existing.ExecuteReader())
            {
                if (row.Read())
                {
                    if (row.GetString(0) != identity.GlobalId) throw new ArgumentException("global_identity_changed");
                    result.Add((identity.Kind, identity.LocalId), row.GetInt64(1));
                    continue;
                }
            }
            // Table identifiers come exclusively from the fixed allowlist above.
            var next = Convert.ToInt64(Scalar($"SELECT MAX(COALESCE((SELECT MAX(id) FROM {table}),0),COALESCE((SELECT MAX(target_id) FROM sync_entity_identities WHERE kind=$kind),0))+1", ("$kind", identity.Kind)));
            Execute("INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id) VALUES($publisher,$kind,$local,$global,$target)",
                ("$publisher", publisher), ("$kind", identity.Kind), ("$local", identity.LocalId.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$global", identity.GlobalId), ("$target", next));
            result.Add((identity.Kind, identity.LocalId), next);
        }
        return result;
    }

    private long Source(string publisher, long localId, string path)
    {
        if (localId <= 0) throw new ArgumentException("invalid_source_id");
        var qualified = PublishedMediaPath.Qualify(publisher, path);
        var existing = Scalar("SELECT source_id FROM sync_source_bindings WHERE publisher_id=$publisher AND local_source_id=$local", ("$publisher", publisher), ("$local", localId));
        if (existing is not null)
        {
            var id = Convert.ToInt64(existing);
            Execute("UPDATE sources SET path=$path WHERE id=$id", ("$path", qualified), ("$id", id));
            Execute("UPDATE sync_source_bindings SET original_path=$path WHERE publisher_id=$publisher AND local_source_id=$local", ("$path", path), ("$publisher", publisher), ("$local", localId));
            return id;
        }
        var source = database.AddSource(qualified);
        Execute("INSERT INTO sync_source_bindings(publisher_id,local_source_id,source_id,source_uuid,original_path) VALUES($publisher,$local,$source,$uuid,$path)",
            ("$publisher", publisher), ("$local", localId), ("$source", source.Id), ("$uuid", Guid.NewGuid().ToString("N")), ("$path", path));
        return source.Id;
    }

    private int ApplySource(string publisher, SpaceSourceBatch source)
    {
        _ = Source(publisher, source.SourceId, source.SourcePath);
        return 1;
    }

    private int ApplyImages(string publisher, SpaceImageBatch batch, Dictionary<(string, long), long> identities)
    {
        if (batch.Images is null || batch.Images.Count > 200) throw new ArgumentException("image_batch_too_large");
        var source = Source(publisher, batch.SourceId, batch.SourcePath);
        foreach (var image in batch.Images)
        {
            if (image is null || string.IsNullOrWhiteSpace(image.Path) || image.SourceId != batch.SourceId || image.FileSize < 0 || !BelowRoot(image.Path, batch.SourcePath)) throw new ArgumentException("invalid_image_source");
            image.Id = Mapped(identities, "image", image.Id); image.SourceId = source;
            image.Path = PublishedMediaPath.Qualify(publisher, image.Path); image.ThumbPath = null;
            if (database.GetImageByPath(image.Path, anyStatus: true) is { } owner && owner.Id != image.Id)
                throw new ArgumentException("path_owned_by_another_identity");
            database.UpsertMirroredImage(image);
        }
        return batch.Images.Count;
    }

    private int ApplyVideos(string publisher, SpaceVideoBatch batch, Dictionary<(string, long), long> identities)
    {
        if (batch.Videos is null || batch.Videos.Count > 100) throw new ArgumentException("video_batch_too_large");
        var source = Source(publisher, batch.SourceId, batch.SourcePath);
        var repository = new VideoRepository(database);
        foreach (var video in batch.Videos)
        {
            if (video is null || string.IsNullOrWhiteSpace(video.Path) || video.SourceId != batch.SourceId || video.FileSize < 0 || !BelowRoot(video.Path, batch.SourcePath)) throw new ArgumentException("invalid_video_source");
            repository.UpsertRemote(video with { Id = Mapped(identities, "video", video.Id), SourceId = source,
                Path = PublishedMediaPath.Qualify(publisher, video.Path) }, source, preserveIdentity: true);
        }
        return batch.Videos.Count;
    }

    private static bool BelowRoot(string path, string root) => path.Replace('\\', '/').StartsWith(root.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    private static long Mapped(Dictionary<(string, long), long> identities, string kind, long id) =>
        identities.TryGetValue((kind, id), out var mapped) ? mapped : throw new ArgumentException("missing_global_identity");
    private string SpaceEpoch() => Scalar("SELECT epoch FROM sync_space_state WHERE id=1 AND state='active'") as string ?? throw new InvalidOperationException("space_fenced");
    private SqliteCommand Sql(string text, params (string Key, object? Value)[] values)
    {
        var command = database.Command(text);
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private object? Scalar(string text, params (string Key, object? Value)[] values) { using var command = Sql(text, values); return command.ExecuteScalar(); }
    private void Execute(string text, params (string Key, object? Value)[] values) { using var command = Sql(text, values); command.ExecuteNonQuery(); }
}
