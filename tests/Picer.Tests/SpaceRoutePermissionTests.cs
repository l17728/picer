using Picer.IndexService.Identity;

namespace Picer.Tests;

/// <summary>
/// Every route the GUI can reach must resolve to a scope, or the account host answers 403.
/// These mappings document the AI/model routes that account-mode deployments need.
/// </summary>
public sealed class SpaceRoutePermissionTests
{
    [Theory]
    [InlineData("POST", "/api/service/v1/video-people/7", "analysis.run")]
    [InlineData("POST", "/api/service/v1/video-keyframes/7", "analysis.run")]
    [InlineData("POST", "/api/service/v1/video-transcribe/7", "analysis.run")]
    [InlineData("GET", "/api/service/v1/video-probe/7", "media.read")]
    [InlineData("GET", "/api/service/v1/video-audio/7", "media.read")]
    [InlineData("POST", "/api/service/v1/face/detect", "analysis.run")]
    [InlineData("POST", "/api/service/v1/face/embed", "analysis.run")]
    [InlineData("POST", "/api/service/v1/face/calibrate", "analysis.run")]
    [InlineData("POST", "/api/service/v1/face/train", "analysis.run")]
    [InlineData("POST", "/api/service/v1/vl", "analysis.run")]
    [InlineData("POST", "/api/service/v1/vl/stream", "analysis.run")]
    [InlineData("POST", "/api/service/v1/asr", "analysis.run")]
    [InlineData("POST", "/api/service/v1/tts", "analysis.run")]
    [InlineData("POST", "/api/service/v1/tts/stream", "analysis.run")]
    [InlineData("GET", "/api/service/v1/training/api/face-training/history", "analysis.run")]
    [InlineData("POST", "/api/service/v1/training/api/face-training/jobs", "analysis.run")]
    [InlineData("POST", "/api/service/v1/faces/training/apply", "index.write")]
    [InlineData("POST", "/api/service/v1/scenes/edit", "index.write")]
    [InlineData("POST", "/api/gateway/v1/search", "index.read")]
    // Standalone-service administration stays closed through the account host.
    [InlineData("PUT", "/api/service/v1/config", null)]
    [InlineData("POST", "/api/service/v1/config/rollback/1", null)]
    [InlineData("GET", "/api/service/v1/tasks", null)]
    public void RequiredScopeMapsGuiReachableRoutes(string method, string path, string? expected) =>
        Assert.Equal(expected, SpaceRoutePermissions.RequiredScope(method, path));
}
