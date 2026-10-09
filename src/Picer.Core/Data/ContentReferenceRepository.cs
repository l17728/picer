using Picer.Core.Models;

namespace Picer.Core.Data;

public sealed record ContentReferenceCounts(string Sha256, int Available, int Missing)
{
    public int Total => Available + Missing;
}

/// <summary>
/// image_contents owns content identity; images are its physical path references.
/// Counts are derived from references, never independently incremented counters,
/// so retries, interrupted scans and mirror imports cannot make them drift.
/// A missing reference is retained until explicitly purged by the user.
/// </summary>
public sealed class ContentReferenceRepository(PicerDatabase db)
{
    public ContentReferenceCounts GetCounts(string sha256)
    {
        using var command = db.Command("""
            SELECT COALESCE(SUM(status='active'),0),COALESCE(SUM(status='missing'),0)
            FROM images WHERE COALESCE(content_sha256,sha256)=$hash
            """);
        command.Parameters.AddWithValue("$hash", NormalizeHash(sha256));
        using var reader = command.ExecuteReader();
        reader.Read();
        return new(NormalizeHash(sha256), reader.GetInt32(0), reader.GetInt32(1));
    }

    public IReadOnlyList<ImageRecord> GetReferences(string sha256)
    {
        using var command = db.Command("SELECT id FROM images WHERE COALESCE(content_sha256,sha256)=$hash ORDER BY status,path,id");
        command.Parameters.AddWithValue("$hash", NormalizeHash(sha256));
        var ids = new List<long>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids.Select(id => db.GetImage(id)!).ToArray();
    }

    public static string NormalizeHash(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new ArgumentException("invalid_content_hash", nameof(sha256));
        return sha256.ToLowerInvariant();
    }
}
