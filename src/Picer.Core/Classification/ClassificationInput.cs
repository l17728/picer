using System.Security.Cryptography;
using System.Text;

namespace Picer.Core.Classification;

public sealed record ClassificationEvidence(string Id, string PayloadSha256, string ProvenanceId);
public sealed record ClassificationDefinitionVersion(string LabelId, long Revision);
public sealed record ClassificationInput(string SpaceEpoch, string OwnerId, string MediaIdentity,
    IReadOnlyList<ClassificationEvidence> Descriptions, IReadOnlyList<ClassificationEvidence> People,
    IReadOnlyList<ClassificationEvidence> Metadata, string ModelVersion, string PreprocessingVersion,
    string PromptVersion, IReadOnlyList<ClassificationDefinitionVersion> Definitions,
    long DiscoveryRevision, string CandidatePolicyVersion);

public static class ClassificationInputFingerprint
{
    public const string FormatVersion = "family-classification-input-v1";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static string Compute(ClassificationInput input) => Convert.ToHexString(SHA256.HashData(Encode(input))).ToLowerInvariant();

    // A scan shares one taxonomy. Reuse its exact canonical bytes without changing
    // the persisted fingerprint format or retaining data across operations.
    internal static Func<ClassificationInput, string> CreateBatchComputer(IReadOnlyList<ClassificationDefinitionVersion> definitions)
    {
        var snapshot = definitions.ToArray();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, leaveOpen: true)) WriteDefinitions(writer, snapshot);
        var encoded = stream.ToArray();
        return input => Convert.ToHexString(SHA256.HashData(Encode(input,
            input.Definitions.SequenceEqual(snapshot) ? encoded : null))).ToLowerInvariant();
    }

    public static byte[] Encode(ClassificationInput input) => Encode(input, null);

    private static byte[] Encode(ClassificationInput input, byte[]? definitions)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        Write(writer, FormatVersion);
        WriteRequired(writer, input.SpaceEpoch);
        WriteRequired(writer, input.OwnerId);
        WriteRequired(writer, NormalizeMedia(input.MediaIdentity));
        WriteEvidence(writer, input.Descriptions);
        WriteEvidence(writer, input.People);
        WriteEvidence(writer, input.Metadata);
        WriteRequired(writer, input.ModelVersion);
        WriteRequired(writer, input.PreprocessingVersion);
        WriteRequired(writer, input.PromptVersion);
        if (definitions is null) WriteDefinitions(writer, input.Definitions);
        else writer.Write(definitions);
        if (input.DiscoveryRevision < 0) throw new ArgumentException("classification_invalid_discovery_revision");
        writer.Write(input.DiscoveryRevision);
        WriteRequired(writer, input.CandidatePolicyVersion);
        writer.Flush();
        return stream.ToArray();
    }

    public static string NormalizeMedia(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.StartsWith("sha256:", StringComparison.Ordinal)) return "sha256:" + NormalizeHash(value[7..]);
        // The existing identity resolver owns verification of this opaque file-version identity.
        if (value.StartsWith("local:", StringComparison.Ordinal) && value.Length > 6) return value;
        throw new ArgumentException("classification_unverified_media_identity");
    }

    internal static void WriteEvidence(BinaryWriter writer, IReadOnlyList<ClassificationEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var entries = new Dictionary<string, ClassificationEvidence>(StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.ProvenanceId);
            var normalized = item with { PayloadSha256 = NormalizeHash(item.PayloadSha256) };
            if (entries.TryGetValue(item.Id, out var previous) && previous != normalized)
                throw new ArgumentException("classification_conflicting_evidence_id");
            entries[item.Id] = normalized;
        }
        writer.Write(entries.Count);
        foreach (var item in entries.Values.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            Write(writer, item.Id); Write(writer, item.PayloadSha256); Write(writer, item.ProvenanceId);
        }
    }

    private static void WriteDefinitions(BinaryWriter writer, IReadOnlyList<ClassificationDefinitionVersion> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var entries = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in definitions)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.LabelId);
            if (item.Revision < 1 || entries.TryGetValue(item.LabelId, out var revision) && revision != item.Revision)
                throw new ArgumentException("classification_conflicting_definition");
            entries[item.LabelId] = item.Revision;
        }
        writer.Write(entries.Count);
        foreach (var item in entries.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Write(writer, item.Key); writer.Write(item.Value);
        }
    }

    private static void WriteRequired(BinaryWriter writer, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Write(writer, value);
    }

    private static void Write(BinaryWriter writer, string? value)
    {
        if (value is null) { writer.Write(-1); return; }
        var bytes = Utf8.GetBytes(value);
        writer.Write(bytes.Length); writer.Write(bytes);
    }

    private static string NormalizeHash(string value)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("classification_invalid_hash");
        return value.ToLowerInvariant();
    }
}

public sealed record ClassificationEmbeddingSpace(string ArtifactSha256, string TokenizerVersion,
    string PreprocessingVersion, int Dimension, string NormalizationVersion)
{
    public bool IsCompatibleWith(ClassificationEmbeddingSpace other) => other is not null
        && Valid() && other.Valid() && Dimension == other.Dimension
        && string.Equals(ArtifactSha256, other.ArtifactSha256, StringComparison.OrdinalIgnoreCase)
        && TokenizerVersion == other.TokenizerVersion && PreprocessingVersion == other.PreprocessingVersion
        && NormalizationVersion == other.NormalizationVersion;

    private bool Valid() => ArtifactSha256 is { Length: 64 } && ArtifactSha256.All(Uri.IsHexDigit)
        && Dimension > 0 && !string.IsNullOrWhiteSpace(TokenizerVersion)
        && !string.IsNullOrWhiteSpace(PreprocessingVersion) && !string.IsNullOrWhiteSpace(NormalizationVersion);
}
