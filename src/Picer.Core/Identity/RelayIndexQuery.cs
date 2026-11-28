using System.Text.Json;

namespace Picer.Core.Identity;

/// <summary>Method, gateway path and optional JSON body travel inside the authenticated ciphertext.</summary>
public sealed record RelayIndexQuery(string Method, string Path, JsonElement? Body = null)
{
    public static bool IsSupported(string method, string path) => method switch
    {
        "GET" => path is "health" or "api/gateway/v1/sources" or "api/gateway/v1/directories"
            or "api/gateway/v1/albums" or "api/gateway/v1/tags" or "api/gateway/v1/browser-metadata",
        "POST" => path is "api/gateway/v1/search" or "api/gateway/v1/video-search",
        _ => false
    };
}
