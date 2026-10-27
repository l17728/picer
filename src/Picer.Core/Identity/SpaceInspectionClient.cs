using System.Net;
using System.Net.Http.Json;

namespace Picer.Core.Identity;

/// <summary>Idempotent inspection RPCs share the paired client's connection and bounded retry policy.</summary>
public sealed class SpaceInspectionClient(HttpClient client)
{
    public Task<SpaceInspectionJob> CreateAsync(string operationId,CancellationToken token=default) =>
        JobAsync("api/sync/v2/inspections",new SpaceInspectionRequest(operationId),operationId,token);
    public Task<SpaceInspectionJob> ReadAsync(string jobId,CancellationToken token=default) =>
        JobAsync("api/sync/v2/inspections/read",new SpaceInspectionJobRequest(jobId),jobId,token);
    public Task<SpaceInspectionJob> CancelAsync(string jobId,CancellationToken token=default) =>
        JobAsync("api/sync/v2/inspections/cancel",new SpaceInspectionJobRequest(jobId),jobId,token);
    public async Task<bool> ReportAsync(SpaceInspectionReceipt receipt,CancellationToken token=default) =>
        (await SendAsync<InspectionAcknowledgement>("api/sync/v2/inspections/receipt",receipt,token).ConfigureAwait(false)).Accepted;

    private async Task<SpaceInspectionJob> JobAsync(string path,object request,string expected,CancellationToken token)
    {
        var job=await SendAsync<SpaceInspectionJob>(path,request,token).ConfigureAwait(false);
        if (job.JobId!=expected || job.Targets is null) throw new InvalidDataException("inspection_job_mismatch");
        return job;
    }

    private async Task<T> SendAsync<T>(string path,object request,CancellationToken token)
    {
        for (var attempt=0;;attempt++)
        {
            try
            {
                using var response=await client.PostAsJsonAsync(path,request,DeviceAuthorization.Json,token).ConfigureAwait(false);
                if (attempt<3 && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or
                    HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    var delay=response.Headers.RetryAfter?.Delta??TimeSpan.FromSeconds(2);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds,0,5)),token).ConfigureAwait(false);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<T>(DeviceAuthorization.Json,token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("inspection_response_missing");
            }
            catch (HttpRequestException exception) when (attempt<3 && exception.StatusCode is null)
            { await Task.Delay(TimeSpan.FromSeconds(2),token).ConfigureAwait(false); }
        }
    }
    private sealed record InspectionAcknowledgement(bool Accepted);
}
