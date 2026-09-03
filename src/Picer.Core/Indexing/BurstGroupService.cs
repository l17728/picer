using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Hashing;
using Picer.Core.Models;

namespace Picer.Core.Indexing;

public sealed record BurstGroupRecord(long Id, long RepresentativeImageId, DateTime StartedUtc,
    DateTime EndedUtc, IReadOnlyList<long> ImageIds);

/// <summary>Builds non-destructive burst-display metadata from capture time and whole-image hashes.</summary>
public sealed class BurstGroupService(PicerDatabase database)
{
    public const string AlgorithmVersion = "time3s-ah4-optional-dh5-v2";
    private static readonly TimeSpan MaximumGap = TimeSpan.FromSeconds(3);

    public int RebuildSource(long sourceId)
    {
        var images = LoadCandidates(sourceId);
        var groups = Build(images);
        using var transaction = database.BeginTransaction();
        using (var clear = database.Command("DELETE FROM burst_groups WHERE source_id=$source"))
        {
            clear.Transaction = transaction;
            clear.Parameters.AddWithValue("$source", sourceId);
            clear.ExecuteNonQuery();
        }
        foreach (var group in groups)
        {
            using var insert = database.Command("""
                INSERT INTO burst_groups(source_id,representative_image_id,started_utc,ended_utc,algorithm_version,updated_utc)
                VALUES($source,$representative,$start,$end,$version,$now); SELECT last_insert_rowid();
                """);
            insert.Transaction = transaction;
            insert.Parameters.AddWithValue("$source", sourceId);
            insert.Parameters.AddWithValue("$representative", group[0].Id);
            insert.Parameters.AddWithValue("$start", Format(EffectiveTime(group[0])));
            insert.Parameters.AddWithValue("$end", Format(EffectiveTime(group[^1])));
            insert.Parameters.AddWithValue("$version", AlgorithmVersion);
            insert.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
            var groupId = (long)insert.ExecuteScalar()!;
            for (var index = 0; index < group.Count; index++)
            {
                using var member = database.Command("""
                    INSERT INTO burst_group_members(group_id,image_id,sequence_index,similarity)
                    VALUES($group,$image,$sequence,$similarity)
                    """);
                member.Transaction = transaction;
                member.Parameters.AddWithValue("$group", groupId);
                member.Parameters.AddWithValue("$image", group[index].Id);
                member.Parameters.AddWithValue("$sequence", index);
                member.Parameters.AddWithValue("$similarity", Similarity(group[0], group[index]));
                member.ExecuteNonQuery();
            }
        }
        using (var state = database.Command("""
            INSERT INTO burst_source_state(source_id,algorithm_version,last_indexed_utc,updated_utc)
            SELECT $source,$version,MAX(indexed_utc),$now FROM images WHERE source_id=$source
            ON CONFLICT(source_id) DO UPDATE SET algorithm_version=excluded.algorithm_version,
                last_indexed_utc=excluded.last_indexed_utc,updated_utc=excluded.updated_utc
            """))
        {
            state.Transaction = transaction;
            state.Parameters.AddWithValue("$source", sourceId);
            state.Parameters.AddWithValue("$version", AlgorithmVersion);
            state.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
            state.ExecuteNonQuery();
        }
        transaction.Commit();
        return groups.Count;
    }

    public bool NeedsRebuild(long sourceId)
    {
        using var command = database.Command("""
            SELECT 1 FROM burst_source_state s
            WHERE s.source_id=$source AND s.algorithm_version=$version
              AND COALESCE(s.last_indexed_utc,'')=COALESCE((SELECT MAX(indexed_utc) FROM images WHERE source_id=$source),'')
            """);
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$version", AlgorithmVersion);
        return command.ExecuteScalar() is null;
    }

    public IReadOnlyDictionary<long, BurstGroupRecord> GetGroupsForImages(IEnumerable<long> imageIds)
    {
        var requested = imageIds.Distinct().ToArray();
        if (requested.Length == 0) return new Dictionary<long, BurstGroupRecord>();
        var result = new Dictionary<long, BurstGroupRecord>();
        foreach (var batch in requested.Chunk(400))
        {
            var parameters = batch.Select((_, index) => $"$id{index}").ToArray();
            using var command = database.Command($"""
                SELECT g.id,g.representative_image_id,g.started_utc,g.ended_utc,m.image_id
                FROM burst_groups g JOIN burst_group_members m ON m.group_id=g.id
                WHERE g.id IN (SELECT group_id FROM burst_group_members WHERE image_id IN ({string.Join(',', parameters)}))
                ORDER BY g.id,m.sequence_index
                """);
            for (var index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(parameters[index], batch[index]);
            using var reader = command.ExecuteReader();
            var builders = new Dictionary<long, (long Representative, DateTime Start, DateTime End, List<long> Members)>();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!builders.TryGetValue(id, out var value))
                    builders[id] = value = (reader.GetInt64(1), Parse(reader.GetString(2)), Parse(reader.GetString(3)), []);
                value.Members.Add(reader.GetInt64(4));
            }
            foreach (var pair in builders)
                result[pair.Key] = new BurstGroupRecord(pair.Key, pair.Value.Representative,
                    pair.Value.Start, pair.Value.End, pair.Value.Members);
        }
        return result;
    }

    private List<ImageRecord> LoadCandidates(long sourceId)
    {
        using var command = database.Command("""
            SELECT * FROM images WHERE source_id=$source AND status='active' AND taken_utc IS NOT NULL
              AND ahash<>0 ORDER BY taken_utc,id
            """);
        command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        var result = new List<ImageRecord>();
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }

    private static List<List<ImageRecord>> Build(IReadOnlyList<ImageRecord> images)
    {
        var result = new List<List<ImageRecord>>();
        var current = new List<ImageRecord>();
        foreach (var image in images)
        {
            if (current.Count == 0 || IsAdjacent(current[^1], image)) current.Add(image);
            else
            {
                if (current.Count >= 2) result.Add(current);
                current = [image];
            }
        }
        if (current.Count >= 2) result.Add(current);
        return result;
    }

    private static bool IsAdjacent(ImageRecord left, ImageRecord right)
    {
        var gap = EffectiveTime(right) - EffectiveTime(left);
        if (gap < TimeSpan.Zero || gap > MaximumGap) return false;
        if (!string.IsNullOrWhiteSpace(left.Camera) && !string.IsNullOrWhiteSpace(right.Camera) &&
            !string.Equals(left.Camera, right.Camera, StringComparison.OrdinalIgnoreCase)) return false;
        if (left.Height <= 0 || right.Height <= 0) return false;
        var leftAspect = (double)left.Width / left.Height;
        var rightAspect = (double)right.Width / right.Height;
        if (Math.Abs(leftAspect - rightAspect) / leftAspect > 0.01 ||
            PerceptualHasher.HammingDistance(left.AverageHash, right.AverageHash) > 4) return false;
        return left.DifferenceHash == 0 || right.DifferenceHash == 0 ||
               PerceptualHasher.HammingDistance(left.DifferenceHash, right.DifferenceHash) <= 5;
    }

    private static double Similarity(ImageRecord left, ImageRecord right)
    {
        if (left.Id == right.Id) return 1;
        var average = 1d - PerceptualHasher.HammingDistance(left.AverageHash, right.AverageHash) / 64d;
        if (left.DifferenceHash == 0 || right.DifferenceHash == 0) return Math.Clamp(average, 0, 1);
        var difference = 1d - PerceptualHasher.HammingDistance(left.DifferenceHash, right.DifferenceHash) / 64d;
        return Math.Clamp((average + difference) / 2, 0, 1);
    }

    private static DateTime EffectiveTime(ImageRecord image) => image.TakenUtc!.Value.ToUniversalTime();
    private static string Format(DateTime value) => value.ToUniversalTime().ToString("o");
    private static DateTime Parse(string value) => DateTime.Parse(value, null,
        System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static ImageRecord Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("id")), SourceId = reader.GetInt64(reader.GetOrdinal("source_id")),
        Path = reader.GetString(reader.GetOrdinal("path")), Width = reader.GetInt32(reader.GetOrdinal("width")),
        Height = reader.GetInt32(reader.GetOrdinal("height")), Camera = reader.IsDBNull(reader.GetOrdinal("camera")) ? null : reader.GetString(reader.GetOrdinal("camera")),
        TakenUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("taken_utc")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        AverageHash = reader.GetInt64(reader.GetOrdinal("ahash")), DifferenceHash = reader.GetInt64(reader.GetOrdinal("dhash"))
    };
}
