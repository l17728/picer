using System.Text.Json;
using Picer.Core.Indexing;

namespace Picer.IndexService;

internal sealed partial class AgentConnectionRegistry
{
    public async Task<MediaConsistencyBatch?> VerifyExistingAsync(MediaConsistencyRequest request, CancellationToken token)
    {
        var sessions = _sessions.Values.ToArray();
        if (sessions.Length != 1 || !sessions[0].Capabilities.Contains(MediaConsistencyVerifier.Capability)) return null;
        var normalized = request with { Items = request.Items.Select(item => item with
        { Root = IndexApiHandlers.NormalizeAgentPath(item.Root), Path = IndexApiHandlers.NormalizeAgentPath(item.Path) }).ToArray() };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var payload = await sessions[0].RequestTaskAsync(MediaConsistencyVerifier.Capability, JsonSerializer.Serialize(normalized, options), deadline.Token);
        if (payload is null) return null;
        var result = JsonSerializer.Deserialize<MediaConsistencyBatch>(payload, options) ?? throw new InvalidDataException("invalid_maintenance_response");
        // Validate source paths before restoring the service's stored path representation.
        var expected = normalized.Items.ToDictionary(item => (item.Id, item.IsVideo));
        var originals = request.Items.ToDictionary(item => (item.Id, item.IsVideo));
        return result with { Items = result.Items.Select(item =>
        {
            if (!expected.TryGetValue((item.Id, item.IsVideo), out var source)) throw new InvalidDataException("maintenance_unknown_reference");
            if (item.Entry is not null && !string.Equals(item.Entry.Path, source.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("maintenance_path_mismatch");
            return item.Entry is null ? item : item with { Entry = item.Entry with { Path = originals[(item.Id, item.IsVideo)].Path } };
        }).ToArray() };
    }
}
