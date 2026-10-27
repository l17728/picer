using Microsoft.Data.Sqlite;
using Picer.Core.Gateway;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    /// <summary>Independent connection for background synchronization; never shares an active UI transaction.</summary>
    // A current live database already owns the shared lifetime lease, so child
    // connections need not scan its WAL again. They still own a lease and verify
    // user_version through SQLite, rejecting changes rather than migrating live data.
    public PicerDatabase OpenIndependentConnection()
    {
        if (_isInMemory) throw new NotSupportedException("independent_connection_requires_file_backed_database");
        return new(_conn.DataSource, staging: false, independent: true);
    }

    // The transactionally written correction journal is durable across restart
    // and is never replaced by remote projection snapshots. Keep restore events
    // too: absence from an ignored-only snapshot is not a deletion/version.
    public IReadOnlyList<GatewayFaceReviewDecision> GetFaceReviewDecisions(SqliteTransaction? transaction = null)
    {
        using var command = Command("""
            WITH decisions AS (
                SELECT face_id, CASE WHEN action IN ('ignore','sync_review_ignored')
                    THEN 'ignored' ELSE 'active' END AS state, created_utc
                FROM face_corrections
                WHERE action IN ('ignore','move_to_unassigned','move','create_and_move',
                    'not_this_person','merge_person','review_active','sync_review_ignored','sync_review_active')
            ), ranked AS (
                SELECT *, ROW_NUMBER() OVER(PARTITION BY face_id ORDER BY created_utc DESC,state DESC) AS rank
                FROM decisions
            ) SELECT face_id,state,created_utc FROM ranked WHERE rank=1 ORDER BY face_id
            """);
        if (transaction is not null) command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        var result = new List<GatewayFaceReviewDecision>();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        reader.Close();
        var effective=result.ToDictionary(item=>item.FaceId);
        foreach(var edit in ReadMetadataEdits(transaction,facesOnly:true))
            if(GetFaceManualEdit(edit.EntityId,transaction) is not null)
                effective[edit.EntityId]=new(edit.EntityId,ParseFaceManualState(edit.Value).State=="ignored"?"ignored":"active",edit.UpdatedUtc);
        return effective.Values.OrderBy(item=>item.FaceId).ToArray();
    }

    public IReadOnlyList<GatewayFaceReviewDecision> MergeFaceReviewDecisions(IReadOnlyList<GatewayFaceReviewDecision> incoming)
    {
        using var transaction = BeginTransaction();
        var result = MergeFaceReviewDecisionsInTransaction(incoming, transaction);
        transaction.Commit();
        return result;
    }

    public IReadOnlyList<GatewayFaceReviewDecision> MergeFaceReviewDecisionsInTransaction(IReadOnlyList<GatewayFaceReviewDecision> incoming, SqliteTransaction transaction)
    {
        MergeFaceReviewDecisions(incoming, transaction);
        var decisions = GetFaceReviewDecisions(transaction);
        ApplyFaceReviewDecisions(decisions, transaction);
        var requested = incoming.Select(item => item.FaceId).ToHashSet();
        return decisions.Where(item => requested.Contains(item.FaceId)).ToArray();
    }

    private void MergeFaceReviewDecisions(IReadOnlyList<GatewayFaceReviewDecision> incoming, SqliteTransaction transaction)
    {
        var current = GetFaceReviewDecisions(transaction).ToDictionary(item => item.FaceId);
        foreach (var item in incoming)
        {
            if (item.FaceId <= 0 || item.State is not ("ignored" or "active") ||
                !DateTime.TryParseExact(item.UpdatedUtc, "O", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var time) || time.Kind != DateTimeKind.Utc)
                throw new ArgumentException("invalid_face_review_decision");
            if(HasFaceManualState(item.FaceId,transaction)) continue;
            if (current.TryGetValue(item.FaceId, out var previous) &&
                (string.CompareOrdinal(previous.UpdatedUtc, item.UpdatedUtc) > 0 ||
                 previous.UpdatedUtc == item.UpdatedUtc && string.CompareOrdinal(previous.State, item.State) >= 0)) continue;
            using var insert = Command("""
                INSERT INTO face_corrections(face_id,action,created_utc)
                SELECT $face,$action,$updated WHERE EXISTS(SELECT 1 FROM detected_faces WHERE id=$face)
                """);
            insert.Transaction = transaction;
            insert.Parameters.AddWithValue("$face", item.FaceId);
            insert.Parameters.AddWithValue("$action", "sync_review_" + item.State);
            insert.Parameters.AddWithValue("$updated", item.UpdatedUtc);
            if (insert.ExecuteNonQuery() > 0) current[item.FaceId] = item;
        }
    }

    private void ApplyFaceReviewDecisions(IReadOnlyList<GatewayFaceReviewDecision> decisions, SqliteTransaction transaction)
    {
        using var ignored = Command("""
            INSERT INTO face_review_states(face_id,state,updated_utc) VALUES($face,'ignored',$updated)
            ON CONFLICT(face_id) DO UPDATE SET state='ignored',updated_utc=excluded.updated_utc
            WHERE face_review_states.state IS NOT 'ignored' OR face_review_states.updated_utc IS NOT excluded.updated_utc;
            DELETE FROM face_person_assignments WHERE face_id=$face;
            DELETE FROM face_candidate_members WHERE face_id=$face;
            """);
        using var active = Command("DELETE FROM face_review_states WHERE face_id=$face");
        ignored.Transaction = active.Transaction = transaction;
        foreach (var item in decisions)
        {
            if(GetFaceManualEdit(item.FaceId,transaction) is { } manual) { ApplyFaceManualState(manual,transaction);continue; }
            var command = item.State == "ignored" ? ignored : active;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$face", item.FaceId);
            if (item.State == "ignored") command.Parameters.AddWithValue("$updated", item.UpdatedUtc);
            command.ExecuteNonQuery();
        }
    }
}
