using System.Net;
using System.Net.Http.Json;
using Picer.Core.Data;

namespace Picer.Core.Gateway;

public sealed partial class IndexSyncClient
{
    private bool? _descriptionDeltaSupported;
    // Serialize with full mirror reads at the application boundary, so a read begun
    // before this confirmation cannot subsequently reconcile away newer results.
    public async Task<bool> PullDescriptionChangesAsync(PicerDatabase database, CancellationToken token = default)
    {
        if (!accountMode) return false;
        if (_descriptionDeltaSupported is null)
        {
            using var capabilities = await client.GetAsync("health", token).ConfigureAwait(false);
            if (capabilities.StatusCode == HttpStatusCode.NotFound) { _descriptionDeltaSupported = false; return false; }
            capabilities.EnsureSuccessStatusCode();
            using var document = await System.Text.Json.JsonDocument.ParseAsync(await capabilities.Content.ReadAsStreamAsync(token).ConfigureAwait(false), cancellationToken: token).ConfigureAwait(false);
            _descriptionDeltaSupported = document.RootElement.TryGetProperty("descriptionDelta", out var supported) && supported.TryGetInt32(out var version) && version == 1;
        }
        if (_descriptionDeltaSupported != true) return false;
        using var independent = database.OpenIndependentConnection();
        DescriptionDeltaCursor? cursor = null;
        await WriteDatabaseAsync(() => cursor = independent.GetDescriptionDeltaCursor(), token).ConfigureAwait(false);
        long? through = null;
        do
        {
            using var response = await client.PostAsJsonAsync("api/sync/v2/descriptions/changes", new DescriptionDeltaRequest(cursor, through), Json, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            if (response.StatusCode == HttpStatusCode.Conflict) throw new InvalidOperationException("description_delta_baseline_required");
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<DescriptionDeltaPage>(Json, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("description_delta_missing");
            if (through is not null && page.Through != through || page.Entries.Count > 20 ||
                page.Cursor.Sequence == (cursor?.Sequence ?? 0) && page.Cursor.Sequence < page.Through)
                throw new InvalidDataException("description_delta_no_progress");
            await WriteDatabaseAsync(() => independent.ApplyDescriptionDelta(page, cursor), token).ConfigureAwait(false);
            cursor = page.Cursor; through = page.Through;
        } while (cursor.Sequence < through);
        return true;
    }
}
