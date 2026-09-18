using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Picer.Core.Data;

namespace Picer.Core.People;

public sealed record FaceSnapshotRecipe(int RandomSeed = 20260904, double TrainRatio = 0.7, double ValidationRatio = 0.2);

public sealed record FaceSnapshotPersonCount(long PersonId, int Train, int Validation, int Test, int NegativeFor);

public sealed record FaceTrainingSnapshotResult(
    long SnapshotId,
    string ManifestHash,
    int MemberCount,
    int NegativePairCount,
    int LeakageGroupCount,
    int PersonCount,
    IReadOnlyList<FaceSnapshotPersonCount> Counts,
    IReadOnlyList<string> Warnings);

public sealed record FaceSnapshotEligibility(int PersonCount, int PositiveFaceCount, IReadOnlyList<string> Warnings)
{
    public bool MeetsMinimumQualification => PersonCount >= 8 && Warnings.Count == 0;
}

public static class FaceTrainingSnapshotService
{
    public const string RuleVersion = "face-curation-v1";
    public const int MinimumPersons = 8;
    public const int MinimumIndependentGroupsPerPerson = 5;

    private sealed record LabeledFace(long FaceId, long ImageId, long PersonId, bool Positive);

    public static FaceSnapshotEligibility CheckEligibility(PicerDatabase db)
    {
        var warnings = new List<string>();
        var (positives, negativePairs) = LoadLabels(db);
        var persons = positives.Select(f => f.PersonId).Distinct().ToList();
        var groups = BuildLeakageGroups(positives, negativePairs, db);
        var groupKeys = new HashSet<long>();
        var insufficient = new List<long>();
        foreach (var person in persons)
        {
            groupKeys.Clear();
            foreach (var face in positives.Where(f => f.PersonId == person))
                if (groups.TryGetValue(face.FaceId, out var group)) groupKeys.Add(group);
            if (groupKeys.Count < MinimumIndependentGroupsPerPerson) insufficient.Add(person);
        }
        if (persons.Count < MinimumPersons)
            warnings.Add($"已确认人物 {persons.Count} 位，低于最低资格 {MinimumPersons} 位，建议降级 prototype-calibration。");
        if (insufficient.Count > 0)
            warnings.Add($"{insufficient.Count} 位人物的独立泄漏组少于 {MinimumIndependentGroupsPerPerson} 组。");
        if (negativePairs.Count == 0)
            warnings.Add("尚无人工确认的困难负样本（face_person_rejections 为空）。");
        return new FaceSnapshotEligibility(persons.Count, positives.Count, warnings);
    }

    public static FaceTrainingSnapshotResult BuildSnapshot(PicerDatabase db, FaceSnapshotRecipe? recipe = null)
    {
        recipe ??= new FaceSnapshotRecipe();
        var (positives, negativePairs) = LoadLabels(db);
        if (positives.Count == 0) throw new InvalidOperationException("库中无已确认人脸，无法构建训练快照。");
        var groups = BuildLeakageGroups(positives, negativePairs, db);
        var splitByGroup = AssignSplits(groups.Values.Distinct(), recipe);

        var createdUtc = DateTime.UtcNow;
        long snapshotId;
        var memberLines = new List<string>();
        using (var transaction = db.BeginTransaction())
        {
            using (var insertSnapshot = db.Command("""
                INSERT INTO face_training_snapshots (created_utc, rule_version, random_seed, status, train_ratio, validation_ratio)
                VALUES ($c, $rv, $seed, 'ready', $tr, $vr); SELECT last_insert_rowid();
                """))
            {
                insertSnapshot.Transaction = transaction;
                insertSnapshot.Parameters.AddWithValue("$c", createdUtc.ToString("o"));
                insertSnapshot.Parameters.AddWithValue("$rv", RuleVersion);
                insertSnapshot.Parameters.AddWithValue("$seed", recipe.RandomSeed);
                insertSnapshot.Parameters.AddWithValue("$tr", recipe.TrainRatio);
                insertSnapshot.Parameters.AddWithValue("$vr", recipe.ValidationRatio);
                snapshotId = (long)insertSnapshot.ExecuteScalar()!;
            }
            foreach (var face in positives)
            {
                var group = groups[face.FaceId];
                var split = splitByGroup[group];
                using var member = db.Command("""
                    INSERT INTO face_snapshot_members (snapshot_id, face_id, image_id, leakage_group, person_id, split)
                    VALUES ($s, $f, $i, $g, $p, $split)
                    ON CONFLICT (snapshot_id, face_id, person_id) DO UPDATE SET split = excluded.split
                    """);
                member.Transaction = transaction;
                member.Parameters.AddWithValue("$s", snapshotId);
                member.Parameters.AddWithValue("$f", face.FaceId);
                member.Parameters.AddWithValue("$i", face.ImageId);
                member.Parameters.AddWithValue("$g", group);
                member.Parameters.AddWithValue("$p", face.PersonId);
                member.Parameters.AddWithValue("$split", split);
                member.ExecuteNonQuery();
                memberLines.Add($"p:{face.FaceId}:{face.PersonId}:{group}:{split}");
            }
            foreach (var (faceId, personId, imageId) in negativePairs)
            {
                var split = splitByGroup[groups[faceId]];
                using var rejection = db.Command("""
                    INSERT OR IGNORE INTO face_snapshot_rejections (snapshot_id, face_id, person_id, split)
                    VALUES ($s, $f, $p, $split)
                    """);
                rejection.Transaction = transaction;
                rejection.Parameters.AddWithValue("$s", snapshotId);
                rejection.Parameters.AddWithValue("$f", faceId);
                rejection.Parameters.AddWithValue("$p", personId);
                rejection.Parameters.AddWithValue("$split", split);
                rejection.ExecuteNonQuery();
                memberLines.Add($"n:{faceId}:{personId}:{split}");
            }
            transaction.Commit();
        }

        memberLines.Sort(StringComparer.Ordinal);
        var manifestSource = $"{RuleVersion}|{recipe.RandomSeed}|{recipe.TrainRatio:F2}|{recipe.ValidationRatio:F2}|" + string.Join(";", memberLines);
        var manifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestSource)));

        var warnings = CheckEligibility(db).Warnings;
        var counts = CountByPerson(db, snapshotId, negativePairs.Count);
        UpdateSnapshotSummary(db, snapshotId, counts, warnings, manifestHash);

        return new FaceTrainingSnapshotResult(
            snapshotId, manifestHash, positives.Count, negativePairs.Count,
            groups.Values.Distinct().Count(), counts.Count, counts, warnings);
    }

    private static (List<LabeledFace> Positives, List<(long FaceId, long PersonId, long ImageId)> NegativePairs) LoadLabels(PicerDatabase db)
    {
        var positives = new List<LabeledFace>();
        using (var cmd = db.Command("""
            SELECT fa.face_id, fa.person_id, f.image_id
            FROM face_person_assignments fa
            JOIN detected_faces f ON f.id = fa.face_id
            WHERE fa.state = 'confirmed'
            """))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                positives.Add(new LabeledFace(reader.GetInt64(0), reader.GetInt64(2), reader.GetInt64(1), true));
        var negativePairs = new List<(long, long, long)>();
        using (var cmd = db.Command("""
            SELECT fr.face_id, fr.person_id, f.image_id
            FROM face_person_rejections fr
            JOIN detected_faces f ON f.id = fr.face_id
            """))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                negativePairs.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        // An explicit “不分类” decision is an unknown-person negative. Keep
        // it in the immutable snapshot with person_id=0 so the platform can
        // measure rejection without assigning it to any named person.
        using (var cmd = db.Command("""
            SELECT rs.face_id, 0, f.image_id
            FROM face_review_states rs
            JOIN detected_faces f ON f.id = rs.face_id
            WHERE rs.state = 'ignored'
              AND NOT EXISTS (SELECT 1 FROM face_person_rejections fr WHERE fr.face_id = rs.face_id)
            """))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                negativePairs.Add((reader.GetInt64(0), 0, reader.GetInt64(2)));
        return (positives, negativePairs);
    }

    private static Dictionary<long, long> BuildLeakageGroups(List<LabeledFace> positives, List<(long FaceId, long PersonId, long ImageId)> negativePairs, PicerDatabase db)
    {
        var labeled = new Dictionary<long, long>();
        foreach (var face in positives) labeled[face.FaceId] = face.ImageId;
        foreach (var (faceId, _, imageId) in negativePairs) labeled[faceId] = imageId;

        var parent = new Dictionary<long, long>();
        long Find(long x)
        {
            if (!parent.ContainsKey(x)) parent[x] = x;
            var root = x;
            while (parent[root] != root) root = parent[root];
            while (parent[x] != root) { var next = parent[x]; parent[x] = root; x = next; }
            return root;
        }
        void Union(long a, long b)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        var byImage = labeled.GroupBy(pair => pair.Value).ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToList());
        foreach (var faces in byImage)
            for (var i = 1; i < faces.Value.Count; i++) Union(faces.Value[0], faces.Value[i]);

        var contentByImage = LoadImageContent(db, byImage.Keys);
        var byContent = contentByImage.Where(kv => kv.Value is not null).GroupBy(kv => kv.Value!).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        foreach (var images in byContent.Values)
            for (var i = 1; i < images.Count; i++)
                if (byImage.TryGetValue(images[i - 1], out var left) && byImage.TryGetValue(images[i], out var right))
                    Union(left[0], right[0]);

        var burstGroupsByImage = LoadBurstGroups(db, byImage.Keys);
        var byBurst = burstGroupsByImage.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        foreach (var images in byBurst.Values)
            for (var i = 1; i < images.Count; i++)
                if (byImage.TryGetValue(images[i - 1], out var left) && byImage.TryGetValue(images[i], out var right))
                    Union(left[0], right[0]);

        var canonical = new Dictionary<long, long>();
        foreach (var group in labeled.Keys.GroupBy(Find))
        {
            var representative = group.Key;
            foreach (var faceId in group) canonical[faceId] = representative;
        }
        return canonical;
    }

    private static Dictionary<long, string?> LoadImageContent(PicerDatabase db, IEnumerable<long> imageIds)
    {
        var result = new Dictionary<long, string?>();
        var ids = imageIds.ToList();
        for (var offset = 0; offset < ids.Count; offset += 500)
        {
            var chunk = ids.Skip(offset).Take(500).ToList();
            using var cmd = db.Command($"SELECT id, content_sha256 FROM images WHERE id IN ({string.Join(",", chunk)})");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetInt64(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        return result;
    }

    private static Dictionary<long, long> LoadBurstGroups(PicerDatabase db, IEnumerable<long> imageIds)
    {
        var result = new Dictionary<long, long>();
        var ids = imageIds.ToList();
        if (ids.Count == 0) return result;
        for (var offset = 0; offset < ids.Count; offset += 500)
        {
            var chunk = ids.Skip(offset).Take(500).ToList();
            using var cmd = db.Command($"SELECT image_id, group_id FROM burst_group_members WHERE image_id IN ({string.Join(",", chunk)})");
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result[reader.GetInt64(0)] = reader.GetInt64(1);
        }
        return result;
    }

    private static Dictionary<long, string> AssignSplits(IEnumerable<long> groupKeys, FaceSnapshotRecipe recipe)
    {
        var result = new Dictionary<long, string>();
        foreach (var group in groupKeys)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{RuleVersion}|{recipe.RandomSeed}|{group}"));
            var bucket = BitConverter.ToUInt32(digest, 0) % 10000;
            var threshold = (int)Math.Round(recipe.TrainRatio * 10000);
            var validationThreshold = threshold + (int)Math.Round(recipe.ValidationRatio * 10000);
            result[group] = bucket < threshold ? "train" : bucket < validationThreshold ? "validation" : "test";
        }
        return result;
    }

    private static List<FaceSnapshotPersonCount> CountByPerson(PicerDatabase db, long snapshotId, int negativePairCount)
    {
        var counts = new Dictionary<long, (int Train, int Validation, int Test)>();
        using (var cmd = db.Command("""
            SELECT person_id, split, COUNT(*) FROM face_snapshot_members
            WHERE snapshot_id = $s GROUP BY person_id, split
            """))
        {
            cmd.Parameters.AddWithValue("$s", snapshotId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var person = reader.GetInt64(0);
                var split = reader.GetString(1);
                var current = counts.TryGetValue(person, out var value) ? value : (0, 0, 0);
                var count = (int)reader.GetInt64(2);
                counts[person] = split switch
                {
                    "train" => (current.Item1 + count, current.Item2, current.Item3),
                    "validation" => (current.Item1, current.Item2 + count, current.Item3),
                    _ => (current.Item1, current.Item2, current.Item3 + count)
                };
            }
        }
        var negativeByPerson = new Dictionary<long, int>();
        using (var cmd = db.Command("""
            SELECT m.person_id, COUNT(*) FROM face_snapshot_rejections r
            JOIN face_snapshot_members m ON m.snapshot_id = r.snapshot_id AND m.face_id = r.face_id
            WHERE r.snapshot_id = $s GROUP BY m.person_id
            """))
        {
            cmd.Parameters.AddWithValue("$s", snapshotId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) negativeByPerson[reader.GetInt64(0)] = reader.GetInt32(1);
        }
        return counts.Select(kv => new FaceSnapshotPersonCount(kv.Key, kv.Value.Item1, kv.Value.Item2, kv.Value.Item3, negativeByPerson.GetValueOrDefault(kv.Key)))
            .OrderBy(count => count.PersonId).ToList();
    }

    private static void UpdateSnapshotSummary(PicerDatabase db, long snapshotId, List<FaceSnapshotPersonCount> counts, IReadOnlyList<string> warnings, string manifestHash)
    {
        using var cmd = db.Command("""
            UPDATE face_training_snapshots
            SET counts_json = $counts, warnings_json = $warnings, manifest_hash = $hash
            WHERE id = $id
            """);
        cmd.Parameters.AddWithValue("$counts", JsonSerializer.Serialize(counts));
        cmd.Parameters.AddWithValue("$warnings", JsonSerializer.Serialize(warnings));
        cmd.Parameters.AddWithValue("$hash", manifestHash);
        cmd.Parameters.AddWithValue("$id", snapshotId);
        cmd.ExecuteNonQuery();
    }
}
