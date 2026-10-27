using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Identity;

namespace Picer.Core.Data;

public sealed record CatalogRetirementCompletion(string State,bool CatalogRemoved);

public sealed partial class PicerDatabase
{
    public bool IsRetiredCatalogIdentity(string publisher,string kind,string globalId)
    {
        using var query=Command("SELECT 1 FROM sync_retired_catalog_identities WHERE publisher_id=$publisher AND kind=$kind AND global_id=$global");
        query.Parameters.AddWithValue("$publisher",publisher);query.Parameters.AddWithValue("$kind",kind);
        query.Parameters.AddWithValue("$global",globalId);return query.ExecuteScalar() is not null;
    }

    // Media receipts alone do not authorize retiring another source. Replays use
    // the durable completion even after a retained business row changes identity.
    public CatalogRetirementCompletion FinalizeCatalogRetirement(string publisher,string operation,
        string kind,string globalId,long expectedMediaCount,SpaceUploadScope scope,SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if(kind is not ("tag" or "album" or "person") || !Guid.TryParseExact(operation,"N",out _)
            || !Guid.TryParseExact(globalId,"N",out _) || expectedMediaCount<0)
            throw new ArgumentException("invalid_catalog_completion");
        if(!scope.Photos&&!scope.Videos || kind=="person"&&!scope.People || kind=="album"&&!scope.Photos)
            throw new UnauthorizedAccessException("catalog_retirement_outside_scope");
        long catalog;string direction;
        using(var query=Command("""
            SELECT p.state FROM sync_publishers p
            WHERE p.publisher_id=$publisher
                AND p.state IN ('publishing','receiving')
                AND EXISTS(SELECT 1 FROM sources s WHERE s.id IN(SELECT value FROM json_each($sources))
                    AND (p.state='publishing' OR EXISTS(SELECT 1 FROM sync_source_bindings b
                        WHERE b.publisher_id=p.publisher_id AND b.source_id=s.id)))
            """))
        {
            query.Transaction=transaction;query.Parameters.AddWithValue("$publisher",publisher);
            query.Parameters.AddWithValue("$sources",JsonSerializer.Serialize(scope.SourceIds));
            using var row=query.ExecuteReader();
            if(!row.Read()) throw new UnauthorizedAccessException("catalog_retirement_outside_scope");
            direction=row.GetString(0);
        }
        using(var excluded=Command("""
            SELECT 1 FROM sync_catalog_retirement_receipts r WHERE r.publisher_id=$publisher
                AND r.operation_id=$operation AND r.kind=$kind AND r.global_id=$global
                AND NOT((r.media_kind='image' AND $photos=1 AND EXISTS(SELECT 1 FROM images i
                    WHERE i.id=r.media_id AND i.source_id IN(SELECT value FROM json_each($sources))
                        AND ($receiving=0 OR EXISTS(SELECT 1 FROM sync_source_bindings b WHERE b.publisher_id=$publisher AND b.source_id=i.source_id))))
                    OR (r.media_kind='video' AND $videos=1 AND EXISTS(SELECT 1 FROM videos v
                    WHERE v.id=r.media_id AND v.source_id IN(SELECT value FROM json_each($sources))
                        AND ($receiving=0 OR EXISTS(SELECT 1 FROM sync_source_bindings b WHERE b.publisher_id=$publisher AND b.source_id=v.source_id))))) LIMIT 1
            """))
        {
            excluded.Transaction=transaction;excluded.Parameters.AddWithValue("$publisher",publisher);
            excluded.Parameters.AddWithValue("$operation",operation);excluded.Parameters.AddWithValue("$kind",kind);
            excluded.Parameters.AddWithValue("$global",globalId);excluded.Parameters.AddWithValue("$sources",JsonSerializer.Serialize(scope.SourceIds));
            excluded.Parameters.AddWithValue("$receiving",direction=="receiving"?1:0);
            excluded.Parameters.AddWithValue("$photos",scope.Photos?1:0);excluded.Parameters.AddWithValue("$videos",scope.Videos?1:0);
            if(excluded.ExecuteScalar() is not null) return new("scope_conflict",false);
        }
        using(var previous=Command("""
            SELECT operation_id,confirmed_media_count,catalog_removed FROM sync_retired_catalog_identities
            WHERE publisher_id=$publisher AND kind=$kind AND global_id=$global
            """))
        {
            previous.Transaction=transaction;previous.Parameters.AddWithValue("$publisher",publisher);
            previous.Parameters.AddWithValue("$kind",kind);previous.Parameters.AddWithValue("$global",globalId);
            using var row=previous.ExecuteReader();
            if(row.Read())
            {
                if(row.GetString(0)!=operation || row.GetInt64(1)!=expectedMediaCount)
                    throw new InvalidDataException("catalog_completion_payload_changed");
                return new("completed",row.GetBoolean(2));
            }
        }
        using(var identity=Command("""
            SELECT CASE WHEN $receiving=1 THEN target_id ELSE CAST(local_key AS INTEGER) END
            FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND global_id=$global
            """))
        {
            identity.Transaction=transaction;identity.Parameters.AddWithValue("$publisher",publisher);
            identity.Parameters.AddWithValue("$kind",kind);identity.Parameters.AddWithValue("$global",globalId);
            identity.Parameters.AddWithValue("$receiving",direction=="receiving"?1:0);
            if(identity.ExecuteScalar() is not long id) throw new UnauthorizedAccessException("catalog_retirement_outside_scope");
            catalog=id;
        }
        using(var receipts=Command("""
            SELECT COUNT(*),COALESCE(SUM(state IN ('applied','delivered','kept','retained')),0),
                COALESCE((SELECT purged_count FROM sync_catalog_purged_receipt_counts
                    WHERE publisher_id=$publisher AND operation_id=$operation AND kind=$kind AND global_id=$global),0)
            FROM sync_catalog_retirement_receipts WHERE publisher_id=$publisher AND operation_id=$operation
                AND kind=$kind AND global_id=$global
            """))
        {
            receipts.Transaction=transaction;receipts.Parameters.AddWithValue("$publisher",publisher);
            receipts.Parameters.AddWithValue("$operation",operation);receipts.Parameters.AddWithValue("$kind",kind);
            receipts.Parameters.AddWithValue("$global",globalId);
            using var row=receipts.ExecuteReader();row.Read();
            if(row.GetInt64(0)+row.GetInt64(2)!=expectedMediaCount || row.GetInt64(1)+row.GetInt64(2)!=expectedMediaCount) return new("pending",false);
        }
        var relations=CatalogRetirementRelationshipsSql(kind);
        var changedRelations=new List<(string Kind,long Id,string Global)>();
        using(var remaining=Command($"""
            SELECT owned.media_kind,owned.media_id,e.global_id FROM ({relations}) owned JOIN sync_entity_identities e
                ON e.publisher_id=$publisher AND e.kind=owned.media_kind
                    AND CASE WHEN $receiving=1 THEN e.target_id ELSE CAST(e.local_key AS INTEGER) END=owned.media_id
            WHERE NOT EXISTS(SELECT 1 FROM sync_catalog_retirement_receipts reviewed
                WHERE reviewed.publisher_id=$publisher AND reviewed.operation_id=$operation
                    AND reviewed.kind=$kind AND reviewed.global_id=$global
                    AND reviewed.media_kind=owned.media_kind AND reviewed.media_id=owned.media_id AND reviewed.state='kept')
                AND NOT EXISTS(SELECT 1 FROM sync_catalog_completion_reviews reviewed
                    WHERE reviewed.publisher_id=$publisher AND reviewed.operation_id=$operation
                        AND reviewed.kind=$kind AND reviewed.global_id=$global
                        AND reviewed.media_kind=owned.media_kind AND reviewed.media_id=owned.media_id AND reviewed.state='kept')
            ORDER BY owned.media_kind,owned.media_id LIMIT 200
            """))
        {
            remaining.Transaction=transaction;remaining.Parameters.AddWithValue("$id",catalog);
            remaining.Parameters.AddWithValue("$operation",operation);remaining.Parameters.AddWithValue("$kind",kind);
            remaining.Parameters.AddWithValue("$global",globalId);
            remaining.Parameters.AddWithValue("$publisher",publisher);remaining.Parameters.AddWithValue("$receiving",direction=="receiving"?1:0);
            using var rows=remaining.ExecuteReader();while(rows.Read()) changedRelations.Add((rows.GetString(0),rows.GetInt64(1),rows.GetString(2)));
        }
        if(changedRelations.Count>0)
        {
            using var review=Command("""
                INSERT INTO sync_catalog_completion_reviews
                    (publisher_id,operation_id,kind,global_id,media_kind,media_global_id,media_id,catalog_id,current_fingerprint,created_utc)
                VALUES($publisher,$operation,$kind,$global,$mediaKind,$mediaGlobal,$media,$catalog,$fingerprint,$now)
                ON CONFLICT(publisher_id,operation_id,media_kind,media_global_id) DO UPDATE SET
                    state='pending',current_fingerprint=excluded.current_fingerprint
                WHERE sync_catalog_completion_reviews.state<>'pending'
                    OR sync_catalog_completion_reviews.current_fingerprint<>excluded.current_fingerprint
                """);
            review.Transaction=transaction;
            foreach(var changed in changedRelations)
            {
                review.Parameters.Clear();review.Parameters.AddWithValue("$publisher",publisher);review.Parameters.AddWithValue("$operation",operation);
                review.Parameters.AddWithValue("$kind",kind);review.Parameters.AddWithValue("$global",globalId);
                review.Parameters.AddWithValue("$mediaKind",changed.Kind);review.Parameters.AddWithValue("$mediaGlobal",changed.Global);
                review.Parameters.AddWithValue("$media",changed.Id);review.Parameters.AddWithValue("$catalog",catalog);
                review.Parameters.AddWithValue("$fingerprint",GetCatalogRetirementFingerprint(kind,catalog,changed.Kind,changed.Id,transaction,publisher));
                review.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));review.ExecuteNonQuery();
            }
            return new("conflict",false);
        }
        bool remove;
        using(var retained=Command($"""
            SELECT EXISTS(SELECT 1 FROM ({relations})) OR EXISTS(
                SELECT 1 FROM sync_active_entity_identities e JOIN sync_publishers p ON p.publisher_id=e.publisher_id
                WHERE e.kind=$kind AND NOT(e.publisher_id=$publisher AND e.global_id=$global)
                    AND CASE p.state WHEN 'receiving' THEN e.target_id ELSE CAST(e.local_key AS INTEGER) END=$id)
            """))
        {
            retained.Transaction=transaction;retained.Parameters.AddWithValue("$id",catalog);
            retained.Parameters.AddWithValue("$kind",kind);retained.Parameters.AddWithValue("$publisher",publisher);
            retained.Parameters.AddWithValue("$global",globalId);remove=Convert.ToInt64(retained.ExecuteScalar())==0;
        }
        using(var record=Command("""
            INSERT INTO sync_retired_catalog_identities
                (publisher_id,kind,global_id,operation_id,confirmed_media_count,catalog_removed,created_utc)
            VALUES($publisher,$kind,$global,$operation,$count,$removed,$now);
            UPDATE sync_catalog_retirements SET state='completed'
                WHERE publisher_id=$publisher AND operation_id=$operation AND kind=$kind AND global_id=$global;
            DELETE FROM sync_catalog_purged_receipt_counts WHERE publisher_id=$publisher AND operation_id=$operation;
            DELETE FROM sync_adoption_scan_checkpoints WHERE publisher_id=$publisher;
            """))
        {
            record.Transaction=transaction;record.Parameters.AddWithValue("$publisher",publisher);
            record.Parameters.AddWithValue("$kind",kind);record.Parameters.AddWithValue("$global",globalId);
            record.Parameters.AddWithValue("$operation",operation);record.Parameters.AddWithValue("$count",expectedMediaCount);
            record.Parameters.AddWithValue("$removed",remove?1:0);record.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));record.ExecuteNonQuery();
        }
        if(remove)
        {
            var table=kind switch { "tag"=>"tags","album"=>"albums",_=>"people" };
            using var delete=Command($"DELETE FROM {table} WHERE id=$id");
            delete.Transaction=transaction;delete.Parameters.AddWithValue("$id",catalog);delete.ExecuteNonQuery();
        }
        return new("completed",remove);
    }

    private static string CatalogRetirementRelationshipsSql(string kind)=>kind switch
    {
        "tag"=>"SELECT 'image' media_kind,image_id media_id FROM image_tags WHERE tag_id=$id UNION SELECT 'video',video_id FROM video_effective_tags WHERE tag_id=$id",
        "album"=>"SELECT 'image' media_kind,image_id media_id FROM album_images WHERE album_id=$id",
        "person"=>"""
            SELECT 'image' media_kind,f.image_id media_id FROM detected_faces f JOIN face_person_assignments a ON a.face_id=f.id WHERE a.person_id=$id
            UNION SELECT 'image',f.image_id FROM detected_faces f JOIN face_person_rejections r ON r.face_id=f.id WHERE r.person_id=$id
            UNION SELECT 'video',reference_id FROM video_effective_faces WHERE assigned_person_id=$id OR suggested_person_id=$id
            UNION SELECT 'video',f.reference_id FROM video_effective_faces f JOIN video_face_person_rejections r ON r.video_face_id=f.id WHERE r.person_id=$id
            """,
        _=>throw new ArgumentException("invalid_catalog_kind",nameof(kind))
    };
}
