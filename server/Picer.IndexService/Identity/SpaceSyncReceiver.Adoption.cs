using System.Text.Json;
using Picer.Core.Gateway;
using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncReceiver
{
    private const string AdoptionCandidates = """
        WITH owned AS (
            SELECT e.global_id,e.target_id,'image' media_kind,NULL content_id,NULL owner_key FROM sync_entity_identities e
            JOIN images i ON i.id=e.target_id
            JOIN sync_source_bindings b ON b.publisher_id=e.publisher_id AND b.source_id=i.source_id
            WHERE e.publisher_id=$publisher AND e.kind='image' AND $photos=1
              AND b.local_source_id IN (SELECT value FROM json_each($sources))
            UNION ALL
            SELECT e.global_id,e.target_id,'video',v.content_id,(SELECT owner_key FROM sync_video_owners WHERE video_id=v.id) FROM sync_entity_identities e
            JOIN videos v ON v.id=e.target_id
            JOIN sync_source_bindings b ON b.publisher_id=e.publisher_id AND b.source_id=v.source_id
            WHERE e.publisher_id=$publisher AND e.kind='video' AND $videos=1
              AND b.local_source_id IN (SELECT value FROM json_each($sources))
        ), candidates AS (
            SELECT 'tag' kind,t.tag_id target_id,o.global_id,o.media_kind FROM owned o JOIN image_tags t ON t.image_id=o.target_id WHERE o.media_kind='image'
            UNION ALL SELECT 'tag',t.tag_id,o.global_id,o.media_kind FROM owned o JOIN video_effective_tags t ON t.video_id=o.target_id WHERE o.media_kind='video'
            UNION ALL SELECT 'album',a.album_id,o.global_id,o.media_kind FROM owned o JOIN album_images a ON a.image_id=o.target_id WHERE o.media_kind='image'
            UNION ALL SELECT 'face',f.id,o.global_id,o.media_kind FROM owned o JOIN detected_faces f ON f.image_id=o.target_id WHERE $people=1 AND o.media_kind='image'
            UNION ALL SELECT 'face-cluster',c.id,o.global_id,o.media_kind FROM owned o
                JOIN detected_faces f ON f.image_id=o.target_id JOIN face_candidate_members m ON m.face_id=f.id
                JOIN face_candidate_clusters c ON c.id=m.cluster_id AND c.model_version=f.model_version
                WHERE $people=1 AND o.media_kind='image'
            UNION ALL SELECT 'person',a.person_id,o.global_id,o.media_kind FROM owned o JOIN detected_faces f ON f.image_id=o.target_id
                JOIN face_person_assignments a ON a.face_id=f.id WHERE $people=1 AND o.media_kind='image'
            UNION ALL SELECT 'person',r.person_id,o.global_id,o.media_kind FROM owned o JOIN detected_faces f ON f.image_id=o.target_id
                JOIN face_person_rejections r ON r.face_id=f.id WHERE $people=1 AND o.media_kind='image'
            UNION ALL SELECT 'person',CAST(e.field AS INTEGER),o.global_id,o.media_kind FROM owned o JOIN detected_faces f ON f.image_id=o.target_id
                JOIN metadata_edits e ON e.kind='face-rejection' AND e.entity_id=f.id
                JOIN people p ON p.id=CAST(e.field AS INTEGER) WHERE $people=1 AND o.media_kind='image'
                    AND NOT EXISTS(SELECT 1 FROM sync_entity_identities mapped JOIN sync_retired_catalog_identities retired
                        ON retired.publisher_id=mapped.publisher_id AND retired.kind=mapped.kind AND retired.global_id=mapped.global_id
                        WHERE mapped.publisher_id=$publisher AND mapped.kind='person' AND mapped.target_id=p.id)
            UNION ALL SELECT 'keyframe',k.id,o.global_id,o.media_kind FROM owned o JOIN video_keyframes k ON k.video_id=o.content_id WHERE o.media_kind='video'
            UNION ALL SELECT 'video-face',f.id,o.global_id,o.media_kind FROM owned o JOIN video_detected_faces f ON f.video_id=o.content_id AND f.owner_key=o.owner_key WHERE $people=1 AND o.media_kind='video'
            UNION ALL SELECT 'person',f.assigned_person_id,o.global_id,o.media_kind FROM owned o JOIN video_detected_faces f ON f.video_id=o.content_id AND f.owner_key=o.owner_key
                WHERE $people=1 AND o.media_kind='video' AND f.assigned_person_id IS NOT NULL
            UNION ALL SELECT 'person',f.suggested_person_id,o.global_id,o.media_kind FROM owned o JOIN video_detected_faces f ON f.video_id=o.content_id AND f.owner_key=o.owner_key
                WHERE $people=1 AND o.media_kind='video' AND f.suggested_person_id IS NOT NULL
            UNION ALL SELECT 'person',r.person_id,o.global_id,o.media_kind FROM owned o
                JOIN video_detected_faces f ON f.video_id=o.content_id AND f.owner_key=o.owner_key
                JOIN video_face_person_rejections r ON r.video_face_id=f.id WHERE $people=1 AND o.media_kind='video'
            UNION ALL SELECT 'person',CAST(e.field AS INTEGER),o.global_id,o.media_kind FROM owned o
                JOIN video_detected_faces f ON f.video_id=o.content_id AND f.owner_key=o.owner_key
                JOIN metadata_edits e ON e.kind='video-face-rejection' AND e.entity_id=f.id
                JOIN people p ON p.id=CAST(e.field AS INTEGER) WHERE $people=1 AND o.media_kind='video'
                    AND NOT EXISTS(SELECT 1 FROM sync_entity_identities mapped JOIN sync_retired_catalog_identities retired
                        ON retired.publisher_id=mapped.publisher_id AND retired.kind=mapped.kind AND retired.global_id=mapped.global_id
                        WHERE mapped.publisher_id=$publisher AND mapped.kind='person' AND mapped.target_id=p.id)
        )
        """;

    public SpaceIdentityOfferPage ReadIdentityOffers(SpaceIdentityOfferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var transaction = database.BeginTransaction();
        ValidateAdoptionPublisher(request.PublisherId, request.Epoch, request.Scope);
        var scopeHash = DeviceAuthorization.Digest(JsonSerializer.Serialize(request.Scope with
        { SourceIds = request.Scope.SourceIds.Distinct().Order().ToArray() }, DeviceAuthorization.Json));
        var revision = request.Epoch + ":" + request.MetadataProtocol + ":" + Convert.ToInt64(Scalar("SELECT revision FROM sync_identity_offer_revision WHERE id=1"))
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Scalar("SELECT 1 FROM sync_adoption_scan_checkpoints WHERE publisher_id=$publisher AND scope_hash=$scope AND revision_key=$revision",
            ("$publisher", request.PublisherId), ("$scope", scopeHash), ("$revision", revision)) is not null)
        {
            transaction.Commit();
            return new(request.PublisherId, request.Epoch, [], false);
        }
        var candidates = new List<(string Kind, long Target, string Media)>();
        using (var query = AdoptionSql(AdoptionCandidates + """
            SELECT c.kind,c.target_id,MIN(c.media_kind||':'||c.global_id) FROM candidates c
            WHERE (c.kind<>'face-cluster' OR $clusters=1) AND NOT EXISTS(SELECT 1 FROM sync_active_entity_identities e WHERE e.publisher_id=$publisher
                AND e.kind=c.kind AND e.target_id=c.target_id)
            GROUP BY c.kind,c.target_id ORDER BY c.kind,c.target_id LIMIT 101
            """, request.PublisherId, request.Scope))
        {
            query.Parameters.AddWithValue("$clusters", request.MetadataProtocol >= 14 ? 1 : 0);
            using var rows = query.ExecuteReader();
            while (rows.Read()) candidates.Add((rows.GetString(0), rows.GetInt64(1), rows.GetString(2)));
        }
        var offers = new List<SpaceIdentityOffer>();
        foreach (var (kind, target, anchor) in candidates.Take(100))
        {
            var separator = anchor.IndexOf(':');
            var mediaKind = anchor[..separator];
            var media = anchor[(separator + 1)..];
            Execute("INSERT OR IGNORE INTO sync_identity_offers(offer_id,publisher_id,kind,target_id) VALUES($offer,$publisher,$kind,$target)",
                ("$offer", Guid.NewGuid().ToString("N")), ("$publisher", request.PublisherId), ("$kind", kind), ("$target", target));
            var offer = (string)Scalar("SELECT offer_id FROM sync_identity_offers WHERE publisher_id=$publisher AND kind=$kind AND target_id=$target",
                ("$publisher", request.PublisherId), ("$kind", kind), ("$target", target))!;
            JsonElement payload;
            if (kind == "face-cluster")
            {
                using var cluster = Sql("SELECT model_version,created_utc FROM face_candidate_clusters WHERE id=$id", ("$id", target));
                using var row = cluster.ExecuteReader();
                if (!row.Read()) throw new InvalidDataException("identity_offer_entity_missing");
                payload = JsonSerializer.SerializeToElement(new GatewayFaceCandidateCluster(0, row.GetString(0), row.GetString(1)), DeviceAuthorization.Json);
            }
            else if (kind == "keyframe")
            {
                using var frame = Sql("SELECT timestamp_seconds,dhash FROM video_keyframes WHERE id=$id", ("$id", target));
                using var row = frame.ExecuteReader();
                if (!row.Read()) throw new InvalidDataException("identity_offer_entity_missing");
                payload = JsonSerializer.SerializeToElement(new GatewayVideoKeyframe(0, 0, row.GetDouble(0), string.Empty, row.GetInt64(1)), DeviceAuthorization.Json);
            }
            else if (kind == "video-face")
            {
                using var face = Sql("""
                    SELECT keyframe_id,timestamp_seconds,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc,model_version
                    FROM video_detected_faces WHERE id=$id
                    """, ("$id", target));
                using var row = face.ExecuteReader();
                if (!row.Read()) throw new InvalidDataException("identity_offer_entity_missing");
                var keyframe = row.GetInt64(0);
                var parent = Scalar("SELECT global_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='keyframe' AND target_id=$target",
                    ("$publisher", request.PublisherId), ("$target", keyframe)) as string;
                if (parent is null)
                {
                    Execute("INSERT OR IGNORE INTO sync_identity_offers(offer_id,publisher_id,kind,target_id) VALUES($offer,$publisher,'keyframe',$target)",
                        ("$offer", Guid.NewGuid().ToString("N")), ("$publisher", request.PublisherId), ("$target", keyframe));
                    parent = (string)Scalar("SELECT offer_id FROM sync_identity_offers WHERE publisher_id=$publisher AND kind='keyframe' AND target_id=$target",
                        ("$publisher", request.PublisherId), ("$target", keyframe))!;
                }
                var geometry = new GatewayVideoFace(0, 0, 0, row.GetDouble(1), row.GetInt32(2), row.GetDouble(3), row.GetDouble(4),
                    row.GetDouble(5), row.GetDouble(6), row.GetDouble(7), null, null, null, null, null, null, row.GetString(8)) { ModelVersion = row.GetString(9) };
                payload = JsonSerializer.SerializeToElement(new SpaceVideoFaceIdentityData(parent, geometry), DeviceAuthorization.Json);
            }
            else if (kind == "face")
            {
                using var face = Sql("""
                    SELECT model_version,ordinal,box_x,box_y,box_width,box_height,confidence,landmarks_json,created_utc,is_archived
                    FROM detected_faces WHERE id=$id
                    """, ("$id", target));
                using var row = face.ExecuteReader();
                if (!row.Read()) throw new InvalidDataException("identity_offer_entity_missing");
                payload = JsonSerializer.SerializeToElement(new GatewayDetectedFace(0, 0, row.GetString(0), row.GetInt32(1),
                    row.GetDouble(2), row.GetDouble(3), row.GetDouble(4), row.GetDouble(5), row.GetDouble(6),
                    row.IsDBNull(7) ? null : row.GetString(7), null, row.GetString(8), row.GetBoolean(9)), DeviceAuthorization.Json);
            }
            else
            {
                var table = IdentityTables[kind]; // Fixed protocol allowlist, never a client SQL identifier.
                var nameColumn = kind == "person" ? "display_name" : "name";
                using var item = Sql($"SELECT {nameColumn},created_utc,{(kind == "album" ? "description" : "NULL")} FROM {table} WHERE id=$id", ("$id", target));
                using var row = item.ExecuteReader();
                if (!row.Read()) throw new InvalidDataException("identity_offer_entity_missing");
                payload = JsonSerializer.SerializeToElement(new SpaceCatalogIdentityData(row.GetString(0), row.GetString(1),
                    row.IsDBNull(2) ? null : row.GetString(2)), DeviceAuthorization.Json);
            }
            offers.Add(new(offer, kind, media, payload, mediaKind));
        }
        if (candidates.Count == 0)
            Execute("""
                INSERT INTO sync_adoption_scan_checkpoints(publisher_id,scope_hash,revision_key) VALUES($publisher,$scope,$revision)
                ON CONFLICT(publisher_id,scope_hash) DO UPDATE SET revision_key=excluded.revision_key
                """, ("$publisher", request.PublisherId), ("$scope", scopeHash), ("$revision", revision));
        transaction.Commit();
        return new(request.PublisherId, request.Epoch, offers, candidates.Count > 100);
    }

    public SpaceIdentityAdoptionReceipt AdoptIdentities(SpaceIdentityAdoptionRequest request)
    {
        if (request?.Items is null || request.Items.Count is < 1 or > 100) throw new ArgumentException("invalid_identity_adoptions");
        using var transaction = database.BeginTransaction();
        ValidateAdoptionPublisher(request.PublisherId, request.Epoch, request.Scope);
        var accepted = new List<SpaceIdentityAdoption>();
        var deferred = new List<SpaceIdentityAdoption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in request.Items)
        {
            if (item is null || item.LocalId <= 0 || !Guid.TryParseExact(item.GlobalId, "N", out _) ||
                !Guid.TryParseExact(item.OfferId, "N", out _) || !seen.Add(item.OfferId) || item.Kind is not ("tag" or "album" or "person" or "face" or "keyframe" or "video-face" or "face-cluster"))
                throw new ArgumentException("invalid_identity_adoption");
            if(database.IsRetiredCatalogIdentity(request.PublisherId,item.Kind,item.GlobalId))
                throw new InvalidOperationException("identity_adoption_conflict");
            var target = Scalar("SELECT target_id FROM sync_identity_offers WHERE offer_id=$offer AND publisher_id=$publisher AND kind=$kind",
                ("$offer", item.OfferId), ("$publisher", request.PublisherId), ("$kind", item.Kind));
            if (target is null) throw new UnauthorizedAccessException("identity_offer_unavailable");
            // Offers confer no enduring access. Recheck the current source scope and live
            // media relationship, including on retries after a lost committed response.
            using (var authorized = AdoptionSql(AdoptionCandidates + "SELECT 1 FROM candidates WHERE kind=$kind AND target_id=$target LIMIT 1",
                request.PublisherId, request.Scope))
            {
                authorized.Parameters.AddWithValue("$kind", item.Kind); authorized.Parameters.AddWithValue("$target", target);
                if (authorized.ExecuteScalar() is null)
                {
                    // A narrowed scope or removed remote relationship must not block
                    // unrelated media. Keep the exact request durable on the source;
                    // do not authorize it under a broader, remembered scope.
                    deferred.Add(item);
                    continue;
                }
            }
            accepted.Add(item);
            var key = item.LocalId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string? retiredGlobal=null;
            using (var existing = Sql("SELECT global_id,target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND local_key=$local",
                ("$publisher", request.PublisherId), ("$kind", item.Kind), ("$local", key)))
            using (var row = existing.ExecuteReader())
            {
                if (row.Read())
                {
                    if(row.GetString(0)==item.GlobalId && row.GetInt64(1)==Convert.ToInt64(target)) continue;
                    if(item.Kind is "tag" or "album" or "person" && database.IsRetiredCatalogIdentity(request.PublisherId,item.Kind,row.GetString(0)))
                        retiredGlobal=row.GetString(0);
                    else throw new InvalidOperationException("identity_adoption_conflict");
                }
            }
            if (Scalar("SELECT 1 FROM sync_active_entity_identities WHERE global_id=$global OR (publisher_id=$publisher AND kind=$kind AND target_id=$target)",
                ("$global", item.GlobalId), ("$publisher", request.PublisherId), ("$kind", item.Kind), ("$target", target)) is not null)
                throw new InvalidOperationException("identity_adoption_conflict");
            Execute(retiredGlobal is null
                ? "INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id) VALUES($publisher,$kind,$local,$global,$target)"
                : "UPDATE sync_entity_identities SET global_id=$global,target_id=$target WHERE publisher_id=$publisher AND kind=$kind AND local_key=$local",
                ("$publisher", request.PublisherId), ("$kind", item.Kind), ("$local", key), ("$global", item.GlobalId), ("$target", target));
            if(retiredGlobal is not null)
                Execute("INSERT INTO sync_catalog_generations(publisher_id,kind,global_id,previous_global_id) VALUES($publisher,$kind,$global,$previous)",
                    ("$publisher",request.PublisherId),("$kind",item.Kind),("$global",item.GlobalId),("$previous",retiredGlobal));
        }
        transaction.Commit();
        return new(request.PublisherId, request.Epoch, accepted, deferred);
    }

    private void ValidateAdoptionPublisher(string publisher, string epoch, SpaceUploadScope scope)
    {
        if (!device.Allows("index.read") || !device.Allows("index.write") || scope?.SourceIds is null ||
            scope.SourceIds.Count is < 1 or > 10000 || scope.SourceIds.Any(id => id <= 0))
            throw new UnauthorizedAccessException("identity_adoption_denied");
        var current = Scalar("SELECT epoch FROM sync_publishers WHERE publisher_id=$publisher AND incarnation_id=$device AND device_grant_id=$grant AND space_id=$space AND state='receiving'",
            ("$publisher", publisher), ("$device", device.DeviceId), ("$grant", device.GrantId), ("$space", device.SpaceId)) as string;
        if (current is null) throw new UnauthorizedAccessException("publisher_unavailable");
        if (current != epoch || epoch != SpaceEpoch()) throw new InvalidOperationException("publisher_epoch_reset_required");
    }

    private Microsoft.Data.Sqlite.SqliteCommand AdoptionSql(string sql, string publisher, SpaceUploadScope scope) => Sql(sql,
        ("$publisher", publisher), ("$sources", JsonSerializer.Serialize(scope.SourceIds)),
        ("$photos", scope.Photos ? 1 : 0), ("$videos", scope.Videos ? 1 : 0), ("$people", scope.People ? 1 : 0));
}
