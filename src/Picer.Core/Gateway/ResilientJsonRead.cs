using System.Net;

namespace Picer.Core.Gateway;

internal static class ResilientJsonRead
{
    // Only use for idempotent reads. A truncated response must be discarded in
    // full before retrying, and all attempts share the same bounded body budget.
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        for (var attempt = 0; ; attempt++)
        {
            try { return await read(deadline.Token).ConfigureAwait(false); }
            catch (Exception exception) when (attempt < 2 && !deadline.IsCancellationRequested && IsTransient(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), deadline.Token).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        IOException => true,
        HttpRequestException request => request.StatusCode is null or HttpStatusCode.RequestTimeout
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
        _ => false
    };
}
