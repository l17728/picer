using System.Net.Http;
using System.Net.Http.Headers;

namespace Picer.MediaAgent;

internal static class ModelRequestAuthorization
{
    internal static void Apply(HttpRequestMessage request, string? configuredKey, Func<string> loadLocalKey)
    {
        var key = configuredKey?.Trim();
        // Automatically discovered machine credentials must never leave this host.
        if (string.IsNullOrWhiteSpace(key) && request.RequestUri is { IsAbsoluteUri: true, IsLoopback: true } uri &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            key = loadLocalKey().Trim();
        if (!string.IsNullOrWhiteSpace(key))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }
}
