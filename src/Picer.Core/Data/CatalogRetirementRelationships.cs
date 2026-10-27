using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Identity;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    private bool HasCatalogRetirementRelationship(string kind,long catalog,string mediaKind,long media,SqliteTransaction transaction)
    {
        var sql=(kind,mediaKind) switch
        {
            ("tag","image")=>"SELECT 1 FROM image_tags WHERE image_id=$media AND tag_id=$catalog",
            ("album","image")=>"SELECT 1 FROM album_images WHERE image_id=$media AND album_id=$catalog",
            ("tag","video")=>"SELECT 1 FROM video_effective_tags WHERE video_id=$media AND tag_id=$catalog",
            ("person","image")=>"""
                SELECT 1 FROM detected_faces f WHERE f.image_id=$media AND
                    (EXISTS(SELECT 1 FROM face_person_assignments a WHERE a.face_id=f.id AND a.person_id=$catalog)
                    OR EXISTS(SELECT 1 FROM face_person_rejections r WHERE r.face_id=f.id AND r.person_id=$catalog)) LIMIT 1
                """,
            ("person","video")=>"""
                SELECT 1 FROM video_effective_faces f WHERE f.reference_id=$media AND
                    (f.assigned_person_id=$catalog OR f.suggested_person_id=$catalog
                    OR EXISTS(SELECT 1 FROM video_face_person_rejections r WHERE r.video_face_id=f.id AND r.person_id=$catalog)) LIMIT 1
                """,
            _=>throw new ArgumentException("invalid_catalog_retirement_reference")
        };
        using var query=Command(sql);query.Transaction=transaction;
        query.Parameters.AddWithValue("$media",media);query.Parameters.AddWithValue("$catalog",catalog);
        return query.ExecuteScalar() is not null;
    }

    private bool CanRetireVideoContentRelationships(string publisher,long media,SpaceUploadScope scope,SqliteTransaction transaction)
    {
        using var query=Command("""
            SELECT 1 FROM sync_video_owners origin JOIN sync_video_owners other
                ON other.content_id=origin.content_id AND other.owner_key=origin.owner_key
            JOIN videos v ON v.id=other.video_id JOIN sync_publishers p ON p.publisher_id=$publisher
            WHERE origin.video_id=$media AND (
                v.source_id NOT IN (SELECT value FROM json_each($sources))
                OR NOT EXISTS(SELECT 1 FROM sync_entity_identities e WHERE e.publisher_id=$publisher AND e.kind='video'
                    AND ((p.state='receiving' AND e.target_id=v.id) OR (p.state='publishing' AND e.local_key=CAST(v.id AS TEXT))))
                OR (p.state='receiving' AND NOT EXISTS(SELECT 1 FROM sync_source_bindings b
                    WHERE b.publisher_id=$publisher AND b.source_id=v.source_id))) LIMIT 1
            """);
        query.Transaction=transaction;query.Parameters.AddWithValue("$publisher",publisher);query.Parameters.AddWithValue("$media",media);
        query.Parameters.AddWithValue("$sources",JsonSerializer.Serialize(scope.SourceIds));return query.ExecuteScalar() is null;
    }

    private IReadOnlyList<MetadataEdit> RemoveCatalogRetirementRelationships(string kind,long catalog,string mediaKind,long media,SqliteTransaction transaction)
    {
        var edits=new List<MetadataEdit>();
        void Record(string editKind,long id,string field,string value)
        {
            var edit=RecordMetadataEditCore(editKind,id,field,value,transaction);
            ApplyMetadataEdit(edit,transaction);edits.Add(edit);
        }
        var catalogField=catalog.ToString(CultureInfo.InvariantCulture);
        if(kind=="tag") Record(mediaKind=="image"?"tag-link":"video-tag-link",media,catalogField,"0");
        else if(kind=="album") Record("album-link",catalog,media.ToString(CultureInfo.InvariantCulture),"0");
        else
        {
            var faces=new List<(long Id,string Model,bool Assigned,bool Rejected)>();
            var sql=mediaKind=="image"?"""
                SELECT f.id,f.model_version,
                    EXISTS(SELECT 1 FROM face_person_assignments a WHERE a.face_id=f.id AND a.person_id=$catalog),
                    EXISTS(SELECT 1 FROM face_person_rejections r WHERE r.face_id=f.id AND r.person_id=$catalog)
                FROM detected_faces f WHERE f.image_id=$media
                """:"""
                SELECT DISTINCT f.id,f.model_version,COALESCE(f.assigned_person_id=$catalog,0),
                    EXISTS(SELECT 1 FROM video_face_person_rejections r WHERE r.video_face_id=f.id AND r.person_id=$catalog)
                FROM video_effective_faces f WHERE f.reference_id=$media
                """;
            using(var query=Command(sql))
            {
                query.Transaction=transaction;query.Parameters.AddWithValue("$catalog",catalog);query.Parameters.AddWithValue("$media",media);
                using var rows=query.ExecuteReader();while(rows.Read()) faces.Add((rows.GetInt64(0),rows.GetString(1),rows.GetBoolean(2),rows.GetBoolean(3)));
            }
            foreach(var face in faces)
            {
                if(face.Assigned) Record(mediaKind=="image"?"face-state":"video-face-state",face.Id,"state",
                    JsonSerializer.Serialize(new FaceManualState(null,"unassigned",face.Model)));
                if(face.Rejected) Record(mediaKind=="image"?"face-rejection":"video-face-rejection",face.Id,catalogField,
                    JsonSerializer.Serialize(new FacePersonRejection(false,face.Model)));
            }
            if(mediaKind=="video")
            {
                using var clear=Command("""
                    UPDATE video_detected_faces SET suggested_person_id=NULL,match_confidence=NULL
                    WHERE suggested_person_id=$catalog AND id IN(SELECT id FROM video_effective_faces WHERE reference_id=$media)
                    """);
                clear.Transaction=transaction;clear.Parameters.AddWithValue("$catalog",catalog);clear.Parameters.AddWithValue("$media",media);clear.ExecuteNonQuery();
            }
        }
        return edits;
    }
}
