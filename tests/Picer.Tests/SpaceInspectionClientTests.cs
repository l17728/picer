using System.Net;
using System.Net.Http.Json;
using Picer.Core.Identity;

namespace Picer.Tests;

public sealed class SpaceInspectionClientTests
{
    [Fact]
    public async Task BusyRetryKeepsTheSameDurableJobIdentity()
    {
        var operation=Guid.NewGuid().ToString("N");
        var seen=new List<string>();
        using var http=new HttpClient(new Handler(async request=>
        {
            var body=await request.Content!.ReadFromJsonAsync<SpaceInspectionRequest>(DeviceAuthorization.Json);
            seen.Add(body!.OperationId);
            if (seen.Count==1)
            {
                var busy=new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                busy.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero); return busy;
            }
            return new(HttpStatusCode.OK) { Content=JsonContent.Create(new SpaceInspectionJob(operation,"completed",[]),options:DeviceAuthorization.Json) };
        })) { BaseAddress=new Uri("https://picer.test/spaces/test/") };
        var result=await new SpaceInspectionClient(http).CreateAsync(operation);
        Assert.Equal(operation,result.JobId); Assert.Equal(new[]{operation,operation},seen);
    }

    [Fact]
    public async Task CancellationStopsRetryAndMismatchedJobIsNeverAccepted()
    {
        var requests=0;
        using var busy=new HttpClient(new Handler(_=>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        })) { BaseAddress=new Uri("https://picer.test/") };
        using var stop=new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new SpaceInspectionClient(busy).CreateAsync(Guid.NewGuid().ToString("N"),stop.Token));
        Assert.InRange(requests,0,1);
        using var wrong=new HttpClient(new Handler(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content=JsonContent.Create(new SpaceInspectionJob(Guid.NewGuid().ToString("N"),"completed",[]),options:DeviceAuthorization.Json) })))
        { BaseAddress=new Uri("https://picer.test/") };
        await Assert.ThrowsAsync<InvalidDataException>(()=>new SpaceInspectionClient(wrong).ReadAsync(Guid.NewGuid().ToString("N")));
    }

    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken) => handler(request);
    }
}
