using Picer.Core.Data;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    // Reserve the identities from the authorized snapshot before the first HTTP batch.
    // A concurrent catalog deletion must see every affected media identity, including
    // media in batches that have not been sent yet. Never reserve from a stale snapshot.
    private static bool ReserveSnapshotIdentities(PicerDatabase database, string snapshotPath,
        string publisher, long revision, long metadataRevision)
    {
        using (var attach = database.Command("ATTACH DATABASE $path AS publication_snapshot"))
        {
            attach.Parameters.AddWithValue("$path", snapshotPath);
            attach.ExecuteNonQuery();
        }
        try
        {
            using var transaction = database.BeginTransaction();
            using (var current = database.Command("SELECT COUNT(*) FROM mirror_revision WHERE id=1 AND revision=$revision AND metadata_revision=$metadata"))
            {
                current.Parameters.AddWithValue("$revision", revision);
                current.Parameters.AddWithValue("$metadata", metadataRevision);
                if (Convert.ToInt64(current.ExecuteScalar()) != 1) return false;
            }
            using var reserve = database.Command("""
                INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id)
                SELECT $publisher,kind,CAST(id AS TEXT),lower(hex(randomblob(16))) FROM (
                    SELECT 'tag' kind,id FROM publication_snapshot.tags
                    UNION ALL SELECT 'album',id FROM publication_snapshot.albums
                    UNION ALL SELECT 'person',id FROM publication_snapshot.people
                    UNION ALL SELECT 'image',id FROM publication_snapshot.images
                    UNION ALL SELECT 'video',id FROM publication_snapshot.videos
                ) candidate
                WHERE NOT EXISTS(SELECT 1 FROM sync_entity_identities existing
                    WHERE existing.publisher_id=$publisher AND existing.kind=candidate.kind
                        AND existing.local_key=CAST(candidate.id AS TEXT));
                """);
            reserve.Parameters.AddWithValue("$publisher", publisher);
            reserve.ExecuteNonQuery();
            // A retired object kept for private references can enter a new explicitly
            // selected scope. Its old global identity must never be reactivated.
            using(var rotate=database.Command("""
                UPDATE sync_entity_identities AS e SET global_id=lower(hex(randomblob(16))),target_id=NULL
                WHERE e.publisher_id=$publisher AND e.kind IN ('tag','album','person')
                    AND EXISTS(SELECT 1 FROM sync_retired_catalog_identities r
                        WHERE r.publisher_id=e.publisher_id AND r.kind=e.kind AND r.global_id=e.global_id)
                    AND ((e.kind='tag' AND (EXISTS(SELECT 1 FROM publication_snapshot.image_tags WHERE tag_id=CAST(e.local_key AS INTEGER))
                        OR EXISTS(SELECT 1 FROM publication_snapshot.video_effective_tags WHERE tag_id=CAST(e.local_key AS INTEGER))))
                        OR (e.kind='album' AND EXISTS(SELECT 1 FROM publication_snapshot.album_images WHERE album_id=CAST(e.local_key AS INTEGER)))
                        OR (e.kind='person' AND (EXISTS(SELECT 1 FROM publication_snapshot.face_person_assignments WHERE person_id=CAST(e.local_key AS INTEGER))
                            OR EXISTS(SELECT 1 FROM publication_snapshot.face_person_rejections WHERE person_id=CAST(e.local_key AS INTEGER))
                            OR EXISTS(SELECT 1 FROM publication_snapshot.video_effective_faces WHERE assigned_person_id=CAST(e.local_key AS INTEGER)
                                OR suggested_person_id=CAST(e.local_key AS INTEGER))
                            OR EXISTS(SELECT 1 FROM publication_snapshot.video_face_person_rejections WHERE person_id=CAST(e.local_key AS INTEGER)))));
                INSERT INTO publication_snapshot.sync_entity_identities(publisher_id,kind,local_key,global_id,target_id)
                SELECT publisher_id,kind,local_key,global_id,target_id FROM main.sync_entity_identities
                WHERE publisher_id=$publisher AND kind IN ('tag','album','person')
                ON CONFLICT(publisher_id,kind,local_key) DO UPDATE SET global_id=excluded.global_id,target_id=excluded.target_id;
                """))
            {
                rotate.Parameters.AddWithValue("$publisher",publisher);rotate.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        }
        finally
        {
            using var detach = database.Command("DETACH DATABASE publication_snapshot");
            detach.ExecuteNonQuery();
        }
    }
}
