namespace Picer.MediaAgent;

internal static class ModelEndpointResolver
{
    internal const string DefaultEndpoint = "http://127.0.0.1:8090/api/vl/chat";

    internal static Uri Resolve(string? configuredEndpoint)
    {
        var endpoint = string.IsNullOrWhiteSpace(configuredEndpoint) ? DefaultEndpoint : configuredEndpoint.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("model endpoint must use HTTP(S) loopback");
        return uri;
    }
}
