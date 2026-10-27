using System.Net;
using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class ResilientJsonReadTests
{
    [Fact]
    public async Task TruncatedReadRetriesTheWholeOperationWithinABoundedAttemptCount()
    {
        var attempts = 0;
        Assert.Equal(7, await ResilientJsonRead.RunAsync<int>(_ => ++attempts < 3
            ? Task.FromException<int>(new IOException("truncated fixture")) : Task.FromResult(7), CancellationToken.None));
        Assert.Equal(3, attempts);
        attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => ResilientJsonRead.RunAsync<int>(_ =>
        {
            attempts++;
            return Task.FromException<int>(new IOException("persistent fixture"));
        }, CancellationToken.None));
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AuthorizationFailuresAreNotRetried(HttpStatusCode status)
    {
        var attempts = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => ResilientJsonRead.RunAsync<int>(_ =>
        {
            attempts++;
            return Task.FromException<int>(new HttpRequestException("fixture", null, status));
        }, CancellationToken.None));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CallerCancellationStopsRetryBackoff()
    {
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResilientJsonRead.RunAsync<int>(_ =>
        {
            attempts++;
            cancellation.CancelAfter(50);
            return Task.FromException<int>(new IOException("truncated fixture"));
        }, cancellation.Token));
        Assert.Equal(1, attempts);
    }
}
