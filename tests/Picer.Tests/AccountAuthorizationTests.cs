using System.Security.Cryptography;
using Picer.Core.Identity;
using Picer.IndexService.Identity;
using Xunit;

namespace Picer.Tests;

public sealed class AccountAuthorizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-auth-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private readonly AccountStore _store;
    private readonly ECDsa _signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string PublicKey => DeviceAuthorization.ExportPublicKey(_device);
    public AccountAuthorizationTests() => _store = new(_root, _clock);

    private (string Account, string Login, DeviceAuthorizationFile File, DeviceSession Session, string Attempt) Enroll(bool editing = false, string clientKind = "gui")
    {
        var login = "user-" + Guid.NewGuid().ToString("N");
        var account = _store.Register(login, "test-password-123!", "我的照片");
        var space = Assert.Single(_store.ListSpaces(account));
        var request = Guid.NewGuid().ToString("N");
        _store.CreateEnrollment(new(request, PublicKey, "测试电脑", clientKind));
        var file = _store.ApproveEnrollment(account, request, space.Id, "https://photos.example", "test-pin", _signing, editing, false);
        var attempt = Guid.NewGuid().ToString("N");
        var session = _store.Redeem(request, file.RedemptionCode, PublicKey, attempt);
        return (account, login, file, session, attempt);
    }

    [Fact]
    public void DifferentAccountsCannotApproveOrReadAnotherSpace()
    {
        var first = Enroll();
        var second = Enroll();
        Assert.Null(_store.Authorize(first.Session.AccessToken, PublicKey, second.Session.SpaceId));
        var request = Guid.NewGuid().ToString("N");
        _store.CreateEnrollment(new(request, PublicKey, "另一台电脑", "gui"));
        Assert.Throws<UnauthorizedAccessException>(() => _store.ApproveEnrollment(second.Account, request,
            first.Session.SpaceId, "https://photos.example", "test-pin", _signing, false, false));
        Assert.Throws<UnauthorizedAccessException>(() => _store.Revoke(second.Account, first.Session.GrantId));
    }

    [Fact]
    public void EditingDoesNotImplicitlyAuthorizeOriginalMediaOrAi()
    {
        var entry = Enroll(true);
        var principal = Assert.IsType<AuthorizedDevice>(_store.Authorize(entry.Session.AccessToken, PublicKey));
        Assert.True(principal.Allows("index.write"));
        Assert.False(principal.Allows("media.read"));
        Assert.False(principal.Allows("analysis.run"));
        Assert.False(principal.Allows("answer.run"));
    }

    [Fact]
    public void RedemptionRetryReturnsSameCredentialsButAnotherAttemptCannotReuseCode()
    {
        var entry = Enroll();
        Assert.Equal(entry.Session, _store.Redeem(entry.File.RequestId, entry.File.RedemptionCode, PublicKey, entry.Attempt));
        Assert.Throws<UnauthorizedAccessException>(() => _store.Redeem(entry.File.RequestId, entry.File.RedemptionCode,
            PublicKey, Guid.NewGuid().ToString("N")));
        Assert.NotNull(_store.Authorize(entry.Session.AccessToken, PublicKey));
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Throws<UnauthorizedAccessException>(() => _store.Redeem(entry.File.RequestId, entry.File.RedemptionCode, PublicKey, entry.Attempt));
        Assert.NotNull(_store.Authorize(entry.Session.AccessToken, PublicKey));
    }

    [Fact]
    public void RefreshRetryIsIdempotentAndReuseRevokesFamily()
    {
        var entry = Enroll();
        Assert.NotNull(_store.Authorize(entry.Session.AccessToken, PublicKey));
        var attempt = Guid.NewGuid().ToString("N");
        var refreshed = _store.Refresh(entry.Session.RefreshToken, PublicKey, attempt);
        Assert.Equal(refreshed, _store.Refresh(entry.Session.RefreshToken, PublicKey, attempt));
        Assert.Null(_store.Authorize(entry.Session.AccessToken, PublicKey));
        Assert.NotNull(_store.Authorize(refreshed.AccessToken, PublicKey));
        Assert.Throws<UnauthorizedAccessException>(() => _store.Refresh(entry.Session.RefreshToken, PublicKey, Guid.NewGuid().ToString("N")));
        Assert.Null(_store.Authorize(refreshed.AccessToken, PublicKey));
    }

    [Fact]
    public void RevocationBlocksAccessRefreshAndCachedRedemption()
    {
        var entry = Enroll();
        Assert.NotNull(_store.Authorize(entry.Session.AccessToken, PublicKey));
        _store.Revoke(entry.Account, entry.Session.GrantId);
        Assert.Null(_store.Authorize(entry.Session.AccessToken, PublicKey));
        Assert.Throws<UnauthorizedAccessException>(() => _store.Refresh(entry.Session.RefreshToken, PublicKey, Guid.NewGuid().ToString("N")));
        Assert.Throws<UnauthorizedAccessException>(() => _store.Redeem(entry.File.RequestId, entry.File.RedemptionCode, PublicKey, entry.Attempt));
    }

    [Fact]
    public void RefreshDoesNotRevokeAuthorizedStreamsButExplicitRevocationDoes()
    {
        var entry = Enroll();
        var device = Assert.IsType<AuthorizedDevice>(_store.Authorize(entry.Session.AccessToken, PublicKey));
        _ = _store.Refresh(entry.Session.RefreshToken, PublicKey, Guid.NewGuid().ToString("N"));
        Assert.True(_store.IsAuthorizationCurrent(device));
        _store.Revoke(entry.Account, device.GrantId);
        Assert.False(_store.IsAuthorizationCurrent(device));
    }

    [Fact]
    public void ChangingPasswordPreservesExistingDeviceAccessAndRefresh()
    {
        var entry = Enroll(clientKind: "agent");
        var authorizedBefore = Assert.IsType<AuthorizedDevice>(_store.Authorize(entry.Session.AccessToken, PublicKey));

        _store.ChangePassword(entry.Account, "test-password-123!", "new-test-password-456!");

        Assert.Equal(entry.Account, _store.Authenticate(entry.Login, "new-test-password-456!"));
        Assert.Null(_store.Authenticate(entry.Login, "test-password-123!"));
        var authorizedAfter = Assert.IsType<AuthorizedDevice>(_store.Authorize(entry.Session.AccessToken, PublicKey));
        Assert.Equal(authorizedBefore.GrantId, authorizedAfter.GrantId);
        Assert.True(_store.IsAuthorizationCurrent(authorizedAfter));
        var refreshed = _store.Refresh(entry.Session.RefreshToken, PublicKey, Guid.NewGuid().ToString("N"));
        Assert.NotNull(_store.Authorize(refreshed.AccessToken, PublicKey));
    }

    [Fact]
    public void DeviceKeyCannotBeReplacedAndPendingGrantExpires()
    {
        var entry = Enroll();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(_store.Authorize(entry.Session.AccessToken, DeviceAuthorization.ExportPublicKey(other)));
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(_store.Authorize(entry.Session.AccessToken, PublicKey));
        Assert.Throws<UnauthorizedAccessException>(() => _store.Refresh(entry.Session.RefreshToken, PublicKey, Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void ProofBindsMethodResourceAndTokenAndRejectsReplay()
    {
        var uri = new Uri("https://photos.example/spaces/one/api/images?offset=20");
        var proof = DeviceProof.Create(_device, "GET", uri, _clock.GetUtcNow(), "test-access");
        var verified = DeviceProof.Validate(proof, "GET", uri, _clock.GetUtcNow(), "test-access");
        Assert.Equal(PublicKey, verified.PublicKey);
        Assert.Throws<InvalidDataException>(() => DeviceProof.Validate(proof, "POST", uri, _clock.GetUtcNow(), "test-access"));
        Assert.Throws<InvalidDataException>(() => DeviceProof.Validate(proof, "GET", new Uri("https://photos.example/spaces/two/api/images"), _clock.GetUtcNow(), "test-access"));
        Assert.Throws<InvalidDataException>(() => DeviceProof.Validate(proof, "GET", uri, _clock.GetUtcNow(), "other-access"));
        _store.AcceptProof(verified);
        Assert.Throws<InvalidDataException>(() => _store.AcceptProof(verified));
    }

    [Fact]
    public void AccountPasswordsAndServiceIdentitySurviveRestart()
    {
        var account = _store.Register("Alice", "test-password-123!", "照片空间");
        var reopened = new AccountStore(_root, _clock);
        Assert.Equal(_store.ServiceId, reopened.ServiceId);
        Assert.Equal(account, reopened.Authenticate("alice", "test-password-123!"));
        Assert.Null(reopened.Authenticate("alice", "wrong"));
        Assert.Single(reopened.ListSpaces(account));
    }

    [Fact]
    public void SignedFileRoundTripRejectsTamperingAndUntrustedKey()
    {
        var entry = Enroll();
        var document = DeviceAuthorization.ReadFileDocument(DeviceAuthorization.WriteFileDocument(entry.File));
        DeviceAuthorization.ValidateFile(document, document.Origin, _store.ServiceId, document.RequestId, PublicKey, _signing, _clock.GetUtcNow());
        Assert.Throws<InvalidDataException>(() => DeviceAuthorization.ValidateFile(document with { Scope = "index.purge" },
            document.Origin, _store.ServiceId, document.RequestId, PublicKey, _signing, _clock.GetUtcNow()));
        using var untrusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidDataException>(() => DeviceAuthorization.ValidateFile(document,
            document.Origin, _store.ServiceId, document.RequestId, PublicKey, untrusted, _clock.GetUtcNow()));
    }

    [Theory]
    [InlineData("GET", "/api/service/v1/config")]
    [InlineData("POST", "/api/gateway/v1/backup")]
    [InlineData("POST", "/api/gateway/v1/scan")]
    [InlineData("POST", "/api/sync/v1/images")]
    [InlineData("DELETE", "/api/gateway/v1/sources/1")]
    public void SpaceRoutesDoNotFallThroughToLegacyAdministrativeOrSyncOperations(string method, string path)
        => Assert.Null(SpaceRoutePermissions.RequiredScope(method, path));

    [Fact]
    public void AiStreamRequiresExplicitAnalysisGrant()
    {
        Assert.Equal("analysis.run", SpaceRoutePermissions.RequiredScope("POST", "/api/ai/v1/chat/stream"));
        Assert.Null(SpaceRoutePermissions.RequiredScope("GET", "/api/ai/v1/chat/stream"));
    }

    [Fact]
    public void PhotoAnalysisInputRequiresExplicitAnalysisGrant()
    {
        Assert.Equal("analysis.run", SpaceRoutePermissions.RequiredScope("POST", "/api/gateway/v1/analysis-input"));
        Assert.Null(SpaceRoutePermissions.RequiredScope("GET", "/api/gateway/v1/analysis-input"));
    }

    [Fact]
    public void VideoKeyframeRoutesRequireMediaReadGrant()
    {
        Assert.Equal("media.read", SpaceRoutePermissions.RequiredScope("GET", "/api/gateway/v1/video-keyframes/123"));
        Assert.Equal("media.read", SpaceRoutePermissions.RequiredScope("GET", "/api/gateway/v1/video-keyframes/by-video/528/0"));
        Assert.Null(SpaceRoutePermissions.RequiredScope("POST", "/api/gateway/v1/video-keyframes/123"));
        Assert.Null(SpaceRoutePermissions.RequiredScope("GET", "/api/gateway/v1/video-keyframes/by-video/0/0"));
    }

    [Fact]
    public void RemoteVideoDescriptionRoutesRequireExplicitScopes()
    {
        Assert.Equal("media.read", SpaceRoutePermissions.RequiredScope("GET", "/api/service/v1/video-probe/123"));
        Assert.Equal("analysis.run", SpaceRoutePermissions.RequiredScope("POST", "/api/service/v1/video-keyframes/123"));
        Assert.Equal("analysis.run", SpaceRoutePermissions.RequiredScope("POST", "/api/service/v1/video-transcribe/123"));
        Assert.Null(SpaceRoutePermissions.RequiredScope("GET", "/api/service/v1/video-transcribe/123"));
        Assert.Null(SpaceRoutePermissions.RequiredScope("POST", "/api/service/v1/video-keyframes/not-an-id"));
    }

    [Theory]
    [InlineData("/api/service/v1/clip/text")]
    [InlineData("/api/service/v1/clip/image")]
    public void ClipEmbeddingRoutesRequireExplicitAnalysisGrant(string path)
    {
        Assert.Equal("analysis.run", SpaceRoutePermissions.RequiredScope("POST", path));
        Assert.Null(SpaceRoutePermissions.RequiredScope("GET", path));
    }

    [Theory]
    [InlineData("GET", "/api/service/v1/classification/items", "index.read")]
    [InlineData("GET", "/api/service/v1/classification/latest", "index.read")]
    [InlineData("POST", "/api/service/v1/classification/begin", "analysis.run")]
    [InlineData("POST", "/api/service/v1/classification/confirm", "index.write")]
    [InlineData("POST", "/api/service/v1/classification/reject", "index.write")]
    [InlineData("POST", "/api/service/v1/classification/remove", "index.write")]
    [InlineData("POST", "/api/service/v1/classification/restore", "index.write")]
    [InlineData("POST", "/api/service/v1/classification/correct", "index.write")]
    [InlineData("POST", "/api/service/v1/classification/clustering/settings", "index.write")]
    public void ClassificationRoutesRequireExplicitScopes(string method, string path, string scope) =>
        Assert.Equal(scope, SpaceRoutePermissions.RequiredScope(method, path));

    [Fact]
    public void AuthorizationDocumentRejectsDuplicatePropertiesAndExcessiveDepth()
    {
        Assert.Throws<InvalidDataException>(() => DeviceAuthorization.ReadDocument<System.Text.Json.JsonElement>("{\"id\":1,\"ID\":2}"u8.ToArray()));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => DeviceAuthorization.ReadDocument<System.Text.Json.JsonElement>("[[[[[[[[[0]]]]]]]]]"u8.ToArray()));
    }

    public void Dispose()
    {
        _device.Dispose(); _signing.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }
}
