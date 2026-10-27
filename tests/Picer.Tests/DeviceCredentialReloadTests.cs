using System.Net;
using System.Security.Cryptography;
using Picer.Core.Identity;

namespace Picer.Tests;

public sealed class DeviceCredentialReloadTests
{
    [Fact]
    public async Task RequestsObserveAnotherClientsCredentialRotationButCannotChangeAccountOrSpace()
    {
        var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var service=Guid.NewGuid().ToString("N"); var space=Guid.NewGuid().ToString("N");
        var session=new DeviceSession(service,space,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),"account",
            "index.read index.write","old-test-access","test-refresh",DateTimeOffset.UtcNow.AddHours(1));
        var original=new DeviceConnection("https://picer.test",service,Convert.ToBase64String(new byte[32]),
            DeviceAuthorization.ExportPublicKey(key),"test-key",Guid.NewGuid().ToString("N"),"test-device","gui",session);
        var persisted=original with { Session=session with { AccessToken="rotated-test-access" } };
        var handler=new Handler();
        using var client=new DeviceAccountClient(original,key,value=>persisted=value,handler,()=>persisted);
        using var response=await client.SpaceClient.GetAsync("api/gateway/v1/counts");
        response.EnsureSuccessStatusCode();
        Assert.Equal("rotated-test-access",handler.Access); Assert.Equal(1,handler.Count);
        Assert.Equal("rotated-test-access",client.Connection.Session!.AccessToken);
        persisted=persisted with { Session=persisted.Session! with { SpaceId=Guid.NewGuid().ToString("N") } };
        await Assert.ThrowsAsync<InvalidOperationException>(()=>client.SpaceClient.GetAsync("api/gateway/v1/counts"));
        Assert.Equal(1,handler.Count);
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal string? Access { get; private set; }
        internal int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Count++; Access=request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
