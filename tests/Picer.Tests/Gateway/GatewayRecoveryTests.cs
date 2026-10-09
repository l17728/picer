using System.Net;
using Picer.Core.Gateway;

namespace Picer.Tests.Gateway;

public sealed class GatewayRecoveryTests
{
    [Theory]
    [InlineData("sources")]
    [InlineData("people")]
    [InlineData("frames")]
    public async Task Read_RecoversAfterTransientTransportFailure(string operation)
    {
        using var handler = new Handler((attempt, _) => attempt < 3
            ? throw new HttpRequestException("transport closed", new IOException("EOF"))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        using var client = new HttpClient(handler);
        using var gateway = new HttpIndexGateway("https://test.invalid", "test", client: client);
        if (operation == "sources") Assert.Empty(await gateway.ListSourcesAsync());
        else if (operation == "people") Assert.Empty(await gateway.ListPeopleAsync());
        else { await using var frame = await gateway.OpenVideoKeyframeAsync(123); Assert.NotNull(frame); }
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    [InlineData(HttpStatusCode.Forbidden, 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]
    public async Task Read_PreservesHttpStatusAndBoundsRetries(HttpStatusCode status, int expectedCalls)
    {
        using var handler = new Handler((_, _) => new HttpResponseMessage(status) { Content = new StringContent("private-payload") });
        using var client = new HttpClient(handler);
        using var gateway = new HttpIndexGateway("https://test.invalid", "test", client: client);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.ListPeopleAsync());
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("private-payload", error.Message);
        Assert.Equal(expectedCalls, handler.Calls);
    }

    [Fact]
    public async Task CanceledRead_DoesNotSendRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("must not send"));
        using var client = new HttpClient(handler);
        using var gateway = new HttpIndexGateway("https://test.invalid", "test", client: client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.ListPeopleAsync(cancellation.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AnalyzePeople_DoesNotDuplicateMutationAfterConnectionLoss()
    {
        using var handler = new Handler((_, request) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            throw new HttpRequestException("lost response");
        });
        using var client = new HttpClient(handler);
        using var gateway = new HttpIndexGateway("https://test.invalid", "test", client: client);
        await Assert.ThrowsAsync<HttpRequestException>(() => gateway.AnalyzeVideoPeopleAsync(123));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AnalyzePeople_ExposesSafeProxyDiagnosticWithoutLeakingRawServiceDetails()
    {
        using var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("{\"error\":\"video_people_task_failed\",\"detail\":\"face_endpoint_not_configured\"}")
        });
        using var client = new HttpClient(handler);
        using var gateway = new HttpIndexGateway("https://test.invalid", "test", client: client);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.AnalyzeVideoPeopleAsync(123));
        Assert.Contains("face_endpoint_not_configured", error.Message, StringComparison.Ordinal);

        using var unsafeHandler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("{\"error\":\"video_people_task_failed\",\"detail\":\"https://internal.invalid/?api_key=private\"}")
        });
        using var unsafeClient = new HttpClient(unsafeHandler);
        using var unsafeGateway = new HttpIndexGateway("https://test.invalid", "test", client: unsafeClient);
        var safeError = await Assert.ThrowsAsync<HttpRequestException>(() => unsafeGateway.AnalyzeVideoPeopleAsync(123));
        Assert.DoesNotContain("private", safeError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("video_people_task_failed", safeError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzePeople_ExplainsSafeModelVersionConflict()
    {
        using var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"error\":\"face_model_mismatch\",\"expected\":\"insightface-arcface\",\"actual\":\"sface\"}")
        });
        using var client = new HttpClient(handler);
        using var gateway = new HttpIndexGateway("https://test.invalid", "test", client: client);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.AnalyzeVideoPeopleAsync(123));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Contains("face_model_mismatch", error.Message, StringComparison.Ordinal);
        Assert.Contains("expected=insightface-arcface, actual=sface", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzePeople_ExposesOnlySafeFrameFailureCount()
    {
        using var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("{\"error\":\"video_face_analysis_incomplete\",\"detail\":\"frames_failed:3\"}")
        });
        using var client = new HttpClient(handler);
        using var gateway = new HttpIndexGateway("https://test.invalid", "test", client: client);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.AnalyzeVideoPeopleAsync(123));

        Assert.Contains("video_face_analysis_incomplete:frames_failed:3", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Handler(Func<int, HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(++Calls, request));
    }
}
