using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Video;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    private async Task AdoptRemoteIdentitiesAsync(PicerDatabase database, SpacePublisher publisher,
        SpaceUploadScope scope, CancellationToken cancellationToken)
    {
        // A lost committed HTTP response leaves the exact local identity and request
        // durable. Confirm those first, before exporting any newly imported object.
        await ConfirmPendingAdoptionsAsync(database, publisher, scope, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            using var response = await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/identity-offers",
                new SpaceIdentityOfferRequest(publisher.PublisherId, publisher.Epoch, scope, SpaceSyncProtocol.MetadataVersion), DeviceAuthorization.Json, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<SpaceIdentityOfferPage>(DeviceAuthorization.Json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("identity_offer_page_missing");
            if (page.PublisherId != publisher.PublisherId || page.Epoch != publisher.Epoch || page.Offers is null || page.Offers.Count > 100 ||
                page.HasMore && page.Offers.Count == 0) throw new InvalidDataException("identity_offer_page_invalid");
            if (page.Offers.Count == 0) return;
            ImportIdentityOffers(database, publisher, scope, page.Offers);
            await ConfirmPendingAdoptionsAsync(database, publisher, scope, cancellationToken).ConfigureAwait(false);
            if (!page.HasMore) return;
        }
    }

    internal static void ImportIdentityOffers(PicerDatabase database, SpacePublisher publisher, SpaceUploadScope scope,
        IReadOnlyList<SpaceIdentityOffer> offers)
    {
        using var transaction = database.BeginTransaction();
        using (var validate = database.Command("SELECT 1 FROM sync_publishers WHERE publisher_id=$publisher AND epoch=$epoch AND state='publishing'"))
        {
            validate.Transaction = transaction; validate.Parameters.AddWithValue("$publisher", publisher.PublisherId);
            validate.Parameters.AddWithValue("$epoch", publisher.Epoch);
            if (validate.ExecuteScalar() is null) throw new InvalidDataException("identity_offer_publisher_mismatch");
        }
        Execute(database, transaction, "INSERT OR IGNORE INTO sync_suppression(id) VALUES(1)");
        foreach (var offer in offers)
        {
            if (offer is null || !Guid.TryParseExact(offer.OfferId, "N", out _) || offer.Kind is not ("tag" or "album" or "person" or "face" or "keyframe" or "video-face" or "face-cluster") ||
                !scope.People && offer.Kind is "person" or "face" or "video-face" or "face-cluster" || offer.Data.ValueKind != JsonValueKind.Object ||
                offer.MediaKind is not ("image" or "video") || offer.Kind is "album" or "face" or "face-cluster" && offer.MediaKind != "image" ||
                offer.Kind is "keyframe" or "video-face" && offer.MediaKind != "video")
                throw new InvalidDataException("identity_offer_invalid");
            using var resolve = database.Command("SELECT local_key FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND global_id=$global");
            resolve.Transaction = transaction; resolve.Parameters.AddWithValue("$publisher", publisher.PublisherId);
            resolve.Parameters.AddWithValue("$kind", offer.MediaKind);
            resolve.Parameters.AddWithValue("$global", offer.MediaGlobalId);
            if (resolve.ExecuteScalar() is not string localKey || !long.TryParse(localKey, out var imageId) ||
                !IsMediaInScope(database, transaction, offer.MediaKind, imageId, scope)) throw new InvalidDataException("identity_offer_source_not_authorized");
            using var pending = database.Command("SELECT 1 FROM sync_pending_identity_adoptions WHERE publisher_id=$publisher AND offer_id=$offer");
            pending.Transaction = transaction; pending.Parameters.AddWithValue("$publisher", publisher.PublisherId);
            pending.Parameters.AddWithValue("$offer", offer.OfferId);
            if (pending.ExecuteScalar() is not null) continue;
            var table = offer.Kind switch { "tag" => "tags", "album" => "albums", "person" => "people",
                "keyframe" => "video_keyframes", "video-face" => "video_detected_faces", "face-cluster" => "face_candidate_clusters", _ => "detected_faces" };
            long localId;
            if (offer.Kind == "face-cluster")
            {
                var cluster = offer.Data.Deserialize<GatewayFaceCandidateCluster>(DeviceAuthorization.Json)
                    ?? throw new InvalidDataException("identity_offer_cluster_missing");
                if (cluster.Id != 0 || string.IsNullOrWhiteSpace(cluster.ModelVersion) || cluster.ModelVersion.Length > 256 ||
                    !DateTimeOffset.TryParse(cluster.CreatedUtc, out _))
                    throw new InvalidDataException("identity_offer_cluster_invalid");
                localId = AllocateAdoptionId(database, transaction, table, offer.Kind);
                Execute(database, transaction, "INSERT INTO face_candidate_clusters(id,model_version,created_utc) VALUES($id,$model,$created)",
                    ("$id", localId), ("$model", cluster.ModelVersion), ("$created", cluster.CreatedUtc));
            }
            else if (offer.Kind == "keyframe")
            {
                var frame = offer.Data.Deserialize<GatewayVideoKeyframe>(DeviceAuthorization.Json) ?? throw new InvalidDataException("identity_offer_frame_missing");
                if (frame.Id != 0 || frame.VideoId != 0 || frame.Path != string.Empty || !double.IsFinite(frame.TimestampSeconds) || frame.TimestampSeconds < 0)
                    throw new InvalidDataException("identity_offer_frame_invalid");
                var content = new VideoContentRepository(database).GetContentId(imageId);
                using var existing = database.Command("SELECT id,dhash FROM video_keyframes WHERE video_id=$content AND timestamp_seconds=$time");
                existing.Transaction = transaction; existing.Parameters.AddWithValue("$content", content); existing.Parameters.AddWithValue("$time", frame.TimestampSeconds);
                localId = 0;
                using (var row = existing.ExecuteReader())
                    if (row.Read())
                    {
                        if (row.GetInt64(1) != frame.DifferenceHash) throw new InvalidDataException("identity_adoption_frame_conflict");
                        localId = row.GetInt64(0);
                    }
                if (localId == 0)
                {
                    localId = AllocateAdoptionId(database, transaction, table, offer.Kind);
                    database.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], []) with
                    { VideoKeyframes = [frame with { Id = localId, VideoId = imageId }] }, externalTransaction: transaction);
                }
            }
            else if (offer.Kind == "video-face")
            {
                var item = offer.Data.Deserialize<SpaceVideoFaceIdentityData>(DeviceAuthorization.Json) ?? throw new InvalidDataException("identity_offer_video_face_missing");
                var face = item.Face;
                if (face is null || !Guid.TryParseExact(item.KeyframeGlobalId, "N", out _) || face.Id != 0 || face.VideoId != 0 || face.KeyframeId != 0 || face.Embedding is not null ||
                    face.AssignedPersonId is not null || face.SuggestedPersonId is not null || face.AssignmentState is not null ||
                    face.Ordinal < 0 || !double.IsFinite(face.TimestampSeconds) || face.TimestampSeconds < 0 ||
                    !double.IsFinite(face.BoxX) || !double.IsFinite(face.BoxY) || !double.IsFinite(face.BoxWidth) || !double.IsFinite(face.BoxHeight) ||
                    face.BoxWidth <= 0 || face.BoxHeight <= 0 || !double.IsFinite(face.Confidence) || face.Confidence is < 0 or > 1 || string.IsNullOrWhiteSpace(face.ModelVersion))
                    throw new InvalidDataException("identity_offer_video_face_invalid");
                var content = new VideoContentRepository(database).GetContentId(imageId);
                using var parent = database.Command("""
                    SELECT k.id FROM sync_entity_identities e JOIN video_keyframes k ON k.id=CAST(e.local_key AS INTEGER)
                    WHERE e.publisher_id=$publisher AND e.kind='keyframe' AND e.global_id=$global AND k.video_id=$content
                    """);
                parent.Transaction = transaction; parent.Parameters.AddWithValue("$publisher", publisher.PublisherId);
                parent.Parameters.AddWithValue("$global", item.KeyframeGlobalId); parent.Parameters.AddWithValue("$content", content);
                if (parent.ExecuteScalar() is not long frameId) throw new InvalidDataException("identity_offer_frame_dependency_missing");
                using var existing = database.Command("SELECT id,box_x,box_y,box_width,box_height FROM video_detected_faces WHERE video_id=$content AND keyframe_id=$frame AND model_version=$model AND ordinal=$ordinal AND owner_key=$owner");
                existing.Transaction = transaction; existing.Parameters.AddWithValue("$content", content); existing.Parameters.AddWithValue("$frame", frameId);
                existing.Parameters.AddWithValue("$model", face.ModelVersion); existing.Parameters.AddWithValue("$ordinal", face.Ordinal);
                existing.Parameters.AddWithValue("$owner",database.GetVideoOwnerKey(imageId,transaction));
                localId = 0;
                using (var row = existing.ExecuteReader())
                    if (row.Read())
                    {
                        if (row.GetDouble(1) != face.BoxX || row.GetDouble(2) != face.BoxY || row.GetDouble(3) != face.BoxWidth || row.GetDouble(4) != face.BoxHeight)
                            throw new InvalidDataException("identity_adoption_face_conflict");
                        localId = row.GetInt64(0);
                    }
                if (localId == 0)
                {
                    localId = AllocateAdoptionId(database, transaction, table, offer.Kind);
                    database.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], []) with
                    { VideoFaces = [face with { Id = localId, VideoId = imageId, KeyframeId = frameId }] }, externalTransaction: transaction);
                }
            }
            else if (offer.Kind == "face")
            {
                var face = offer.Data.Deserialize<GatewayDetectedFace>(DeviceAuthorization.Json) ?? throw new InvalidDataException("identity_offer_face_missing");
                if (face.Embedding is not null || face.Id != 0 || face.ImageId != 0 || face.Ordinal < 0 ||
                    !double.IsFinite(face.BoxX) || !double.IsFinite(face.BoxY) || !double.IsFinite(face.BoxWidth) || !double.IsFinite(face.BoxHeight) ||
                    face.BoxWidth <= 0 || face.BoxHeight <= 0 || face.Confidence is < 0 or > 1 || !double.IsFinite(face.Confidence) ||
                    string.IsNullOrWhiteSpace(face.ModelVersion)) throw new InvalidDataException("identity_offer_face_invalid");
                using var existing = database.Command("SELECT id,box_x,box_y,box_width,box_height FROM detected_faces WHERE image_id=$image AND model_version=$model AND ordinal=$ordinal");
                existing.Transaction = transaction; existing.Parameters.AddWithValue("$image", imageId);
                existing.Parameters.AddWithValue("$model", face.ModelVersion); existing.Parameters.AddWithValue("$ordinal", face.Ordinal);
                localId = 0;
                using (var row = existing.ExecuteReader())
                    if (row.Read())
                    {
                        if (row.GetDouble(1) != face.BoxX || row.GetDouble(2) != face.BoxY || row.GetDouble(3) != face.BoxWidth || row.GetDouble(4) != face.BoxHeight)
                            throw new InvalidDataException("identity_adoption_face_conflict");
                        localId = row.GetInt64(0);
                    }
                if (localId == 0)
                {
                    localId = AllocateAdoptionId(database, transaction, table, offer.Kind);
                    database.UpsertMirroredFace(face with { Id = localId, ImageId = imageId }, transaction);
                }
            }
            else
            {
                var item = offer.Data.Deserialize<SpaceCatalogIdentityData>(DeviceAuthorization.Json) ?? throw new InvalidDataException("identity_offer_catalog_missing");
                if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 4096 || !DateTime.TryParse(item.CreatedUtc, out _))
                    throw new InvalidDataException("identity_offer_catalog_invalid");
                localId = 0;
                // Existing tag normalization is a business uniqueness constraint. People
                // and albums deliberately never use their display name as identity.
                if (offer.Kind == "tag")
                {
                    using var tag = database.Command("SELECT id FROM tags WHERE name=$name");
                    tag.Transaction = transaction; tag.Parameters.AddWithValue("$name", item.Name);
                    if (tag.ExecuteScalar() is long id) localId = id;
                }
                if (localId == 0)
                {
                    localId = AllocateAdoptionId(database, transaction, table, offer.Kind);
                    var displayName = item.Name;
                    if (offer.Kind == "person")
                    {
                        using var collision = database.Command("SELECT 1 FROM people WHERE trim(display_name)=trim($name) COLLATE NOCASE");
                        collision.Transaction = transaction; collision.Parameters.AddWithValue("$name", item.Name);
                        if (collision.ExecuteScalar() is not null)
                        {
                            displayName += "（照片空间 " + offer.OfferId + "）";
                            using var label = database.Command("INSERT INTO sync_adopted_person_labels(publisher_id,local_id,remote_name,display_name) VALUES($publisher,$id,$remote,$display)");
                            label.Transaction = transaction; label.Parameters.AddWithValue("$publisher", publisher.PublisherId);
                            label.Parameters.AddWithValue("$id", localId); label.Parameters.AddWithValue("$remote", item.Name);
                            label.Parameters.AddWithValue("$display", displayName); label.ExecuteNonQuery();
                        }
                    }
                    var nameColumn = offer.Kind == "person" ? "display_name" : "name";
                    using var insert = database.Command($"INSERT INTO {table}(id,{nameColumn},created_utc{(offer.Kind == "person" ? ",updated_utc" : offer.Kind == "album" ? ",description" : "")}) VALUES($id,$name,$created{(offer.Kind == "person" ? ",$created" : offer.Kind == "album" ? ",$description" : "")})");
                    insert.Transaction = transaction; insert.Parameters.AddWithValue("$id", localId); insert.Parameters.AddWithValue("$name", displayName);
                    insert.Parameters.AddWithValue("$created", item.CreatedUtc);
                    if (offer.Kind == "album") insert.Parameters.AddWithValue("$description", (object?)item.Description ?? DBNull.Value);
                    insert.ExecuteNonQuery();
                }
            }
            using var map = database.Command("SELECT global_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND local_key=$local");
            map.Transaction = transaction; map.Parameters.AddWithValue("$publisher", publisher.PublisherId); map.Parameters.AddWithValue("$kind", offer.Kind);
            map.Parameters.AddWithValue("$local", localId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var globalId = map.ExecuteScalar() as string ?? offer.OfferId;
            if(database.IsRetiredCatalogIdentity(publisher.PublisherId,offer.Kind,globalId)) globalId=Guid.NewGuid().ToString("N");
            using var save = database.Command("""
                INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id) VALUES($publisher,$kind,$local,$global)
                    ON CONFLICT(publisher_id,kind,local_key) DO UPDATE SET global_id=excluded.global_id
                    WHERE EXISTS(SELECT 1 FROM sync_retired_catalog_identities retired
                        WHERE retired.publisher_id=sync_entity_identities.publisher_id AND retired.kind=sync_entity_identities.kind
                            AND retired.global_id=sync_entity_identities.global_id);
                INSERT INTO sync_pending_identity_adoptions(publisher_id,offer_id,kind,local_id,global_id) VALUES($publisher,$offer,$kind,$id,$global);
                """);
            save.Transaction = transaction; save.Parameters.AddWithValue("$publisher", publisher.PublisherId);
            save.Parameters.AddWithValue("$kind", offer.Kind); save.Parameters.AddWithValue("$local", localId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            save.Parameters.AddWithValue("$id", localId); save.Parameters.AddWithValue("$global", globalId); save.Parameters.AddWithValue("$offer", offer.OfferId);
            save.ExecuteNonQuery();
        }
        Execute(database, transaction, "DELETE FROM sync_suppression WHERE id=1");
        transaction.Commit();
    }

    internal static string PublishedPersonName(PicerDatabase database, string publisher, long localId, string currentName)
    {
        using var query = database.Command("SELECT remote_name FROM sync_adopted_person_labels WHERE publisher_id=$publisher AND local_id=$id AND display_name=$name");
        query.Parameters.AddWithValue("$publisher", publisher); query.Parameters.AddWithValue("$id", localId); query.Parameters.AddWithValue("$name", currentName);
        return query.ExecuteScalar() as string ?? currentName;
    }

    private static MetadataEdit TranslateAdoptedPersonEdit(PicerDatabase database, SqliteTransaction transaction, string publisher, MetadataEdit edit)
    {
        if (edit.Kind != "person" || edit.Field != "name") return edit;
        using var query = database.Command("SELECT display_name FROM sync_adopted_person_labels WHERE publisher_id=$publisher AND local_id=$id AND remote_name=$name");
        query.Transaction = transaction; query.Parameters.AddWithValue("$publisher", publisher); query.Parameters.AddWithValue("$id", edit.EntityId);
        query.Parameters.AddWithValue("$name", edit.Value);
        return query.ExecuteScalar() is string display ? edit with { Value = display } : edit;
    }

    private static long AllocateAdoptionId(PicerDatabase database, SqliteTransaction transaction, string table, string kind)
    {
        using var query = database.Command($"SELECT MAX(COALESCE((SELECT MAX(id) FROM {table}),0),COALESCE((SELECT MAX(CAST(local_key AS INTEGER)) FROM sync_entity_identities WHERE kind=$kind),0),COALESCE((SELECT seq FROM sqlite_sequence WHERE name=$table),0))");
        query.Transaction = transaction; query.Parameters.AddWithValue("$kind", kind); query.Parameters.AddWithValue("$table", table);
        return checked(Convert.ToInt64(query.ExecuteScalar()) + 1);
    }

    private async Task ConfirmPendingAdoptionsAsync(PicerDatabase database, SpacePublisher publisher, SpaceUploadScope scope, CancellationToken cancellationToken)
    {
        var after = "";
        while (true)
        {
            var items = new List<SpaceIdentityAdoption>();
            using (var pending = database.Command("SELECT offer_id,kind,local_id,global_id FROM sync_pending_identity_adoptions WHERE publisher_id=$publisher AND offer_id>$after ORDER BY offer_id LIMIT 100"))
            {
                pending.Parameters.AddWithValue("$publisher", publisher.PublisherId);
                pending.Parameters.AddWithValue("$after", after);
                using var rows = pending.ExecuteReader();
                while (rows.Read()) items.Add(new(rows.GetString(0), rows.GetString(1), rows.GetInt64(2), rows.GetString(3)));
            }
            if (items.Count == 0) return;
            using var response = await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/identity-adoptions",
                new SpaceIdentityAdoptionRequest(publisher.PublisherId, publisher.Epoch, scope, items), DeviceAuthorization.Json, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var receipt = await response.Content.ReadFromJsonAsync<SpaceIdentityAdoptionReceipt>(DeviceAuthorization.Json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("identity_adoption_receipt_missing");
            var confirmed = receipt.Items;
            var deferred = receipt.Deferred ?? [];
            if (receipt.PublisherId != publisher.PublisherId || receipt.Epoch != publisher.Epoch || confirmed is null ||
                confirmed.Count + deferred.Count != items.Count ||
                confirmed.Concat(deferred).Distinct().Count() != items.Count ||
                confirmed.Concat(deferred).Any(item => !items.Contains(item)))
                throw new InvalidDataException("identity_adoption_receipt_mismatch");
            after = items[^1].OfferId;
            if (confirmed.Count == 0) continue;
            using var transaction = database.BeginTransaction();
            foreach (var item in confirmed)
            {
                using var done = database.Command("DELETE FROM sync_pending_identity_adoptions WHERE publisher_id=$publisher AND offer_id=$offer AND global_id=$global");
                done.Transaction = transaction; done.Parameters.AddWithValue("$publisher", publisher.PublisherId);
                done.Parameters.AddWithValue("$offer", item.OfferId); done.Parameters.AddWithValue("$global", item.GlobalId); done.ExecuteNonQuery();
            }
            // An earlier page may have omitted unmapped dependencies. Replay the owned
            // feed after adoption so its prior cursor cannot permanently hide them.
            using var reset = database.Command("UPDATE sync_publishers SET downloaded_sequence=0,downloaded_metadata_version=0,downloaded_metadata_operation='' WHERE publisher_id=$publisher");
            reset.Transaction = transaction; reset.Parameters.AddWithValue("$publisher", publisher.PublisherId); reset.ExecuteNonQuery();
            transaction.Commit();
        }
    }
}
