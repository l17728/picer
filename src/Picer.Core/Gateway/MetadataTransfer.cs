using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Picer.Core.Gateway;

public sealed record MetadataTransfer(Dictionary<string, string> Hashes, JsonObject Changes)
{
    public const string RequestHeader = "X-Picer-Metadata-Hashes";
    public const string ResponseHeader = "X-Picer-Metadata-Delta";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static MetadataTransfer Create(GatewayMetadata metadata, string suppliedHashes)
    {
        Dictionary<string,string> previous;
        try
        {
            previous = suppliedHashes.Length <= 16000
                ? JsonSerializer.Deserialize<Dictionary<string,string>>(Convert.FromBase64String(suppliedHashes)) ?? [] : [];
        }
        catch (Exception error) when (error is FormatException or JsonException) { previous = []; }
        var current = JsonSerializer.SerializeToNode(metadata, Options)!.AsObject();
        var hashes = new Dictionary<string,string>(); var changes = new JsonObject();
        foreach (var field in current)
        {
            var hash = Hash(field.Value);
            hashes[field.Key] = hash;
            if (!previous.TryGetValue(field.Key, out var old) || old != hash) changes[field.Key] = field.Value?.DeepClone();
        }
        return new(hashes, changes);
    }

    public static string Hash(JsonNode? node) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "null")));

    public GatewayMetadata Apply(JsonObject? baseline, out JsonObject snapshot)
    {
        var merged = baseline?.DeepClone().AsObject() ?? new JsonObject();
        foreach (var key in merged.Select(field => field.Key).Where(key => !Hashes.ContainsKey(key)).ToArray()) merged.Remove(key);
        foreach (var field in Changes) merged[field.Key] = field.Value?.DeepClone();
        foreach (var field in Hashes)
            if (!merged.ContainsKey(field.Key) || Hash(merged[field.Key]) != field.Value)
                throw new InvalidDataException("metadata_delta_baseline_mismatch");
        var result = merged.Deserialize<GatewayMetadata>(Options) ?? throw new InvalidDataException("metadata_delta_empty");
        snapshot = merged;
        return result;
    }
}
