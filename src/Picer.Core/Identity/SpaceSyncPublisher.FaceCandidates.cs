using Microsoft.Data.Sqlite;
using Picer.Core.Data;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    private static void ApplyFaceCandidateChange(PicerDatabase database,SqliteTransaction transaction,string publisher,long imageId,SpaceMetadataChange change)
    {
        if(change.FaceCandidateClusters is null || change.FaceCandidateMembers is null)return;
        var members=change.FaceCandidateMembers;var retained=members.Select(member=>member.FaceId).ToHashSet();
        using(var query=database.Command("""
            SELECT m.face_id FROM face_candidate_members m JOIN detected_faces f ON f.id=m.face_id
            JOIN sync_entity_identities e ON e.publisher_id=$publisher AND e.kind='face-cluster' AND e.local_key=CAST(m.cluster_id AS TEXT)
            WHERE f.image_id=$image
            """))
        {
            query.Transaction=transaction;query.Parameters.AddWithValue("$publisher",publisher);query.Parameters.AddWithValue("$image",imageId);
            var remove=new List<long>();using(var rows=query.ExecuteReader())while(rows.Read())if(!retained.Contains(rows.GetInt64(0)))remove.Add(rows.GetInt64(0));
            foreach(var face in remove)Execute(database,transaction,"DELETE FROM face_candidate_members WHERE face_id=$id",("$id",face));
        }
        foreach(var cluster in change.FaceCandidateClusters)
            Execute(database,transaction,"""
                INSERT INTO face_candidate_clusters(id,model_version,created_utc)
                SELECT $id,$model,$created WHERE EXISTS(SELECT 1 FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='face-cluster' AND local_key=CAST($id AS TEXT))
                ON CONFLICT(id) DO UPDATE SET model_version=excluded.model_version,created_utc=excluded.created_utc
                WHERE excluded.created_utc>=face_candidate_clusters.created_utc
                """,("$id",cluster.Id),("$model",cluster.ModelVersion),("$created",cluster.CreatedUtc),("$publisher",publisher));
        foreach(var member in members)
            Execute(database,transaction,"""
                INSERT INTO face_candidate_members(cluster_id,face_id,similarity)
                SELECT $cluster,$face,$similarity WHERE EXISTS(SELECT 1 FROM detected_faces f JOIN face_candidate_clusters c ON c.id=$cluster
                    WHERE f.id=$face AND f.image_id=$image AND f.model_version=c.model_version)
                AND EXISTS(SELECT 1 FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='face-cluster' AND local_key=CAST($cluster AS TEXT))
                ON CONFLICT(face_id) DO UPDATE SET cluster_id=excluded.cluster_id,similarity=excluded.similarity
                WHERE EXISTS(SELECT 1 FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='face-cluster' AND local_key=CAST(face_candidate_members.cluster_id AS TEXT))
                    AND (SELECT created_utc FROM face_candidate_clusters WHERE id=face_candidate_members.cluster_id)<=(SELECT created_utc FROM face_candidate_clusters WHERE id=excluded.cluster_id)
                """,("$cluster",member.ClusterId),("$face",member.FaceId),("$similarity",member.Similarity),("$image",imageId),("$publisher",publisher));
    }
}
