using System.Net;
using System.Text;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class OpenAiCompatibleChatClientTests
{
    [Fact]
    public async Task CompleteAsync_SendsConfiguredModelAndReadsContent()
    {
        string? body = null;
        var handler = new DelegateHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"```json\\n{\\\"ok\\\":true}\\n```\"}}]}", Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleChatClient(new Uri("http://127.0.0.1/chat"), "secret", "vl-model", http);

        var result = await client.CompleteAsync("system", "question");

        Assert.Equal("{\"ok\":true}", result);
        Assert.Contains("\"model\":\"vl-model\"", body);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task CompleteAsync_PropagatesCancellation()
    {
        var handler = new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        using var client = new OpenAiCompatibleChatClient(new Uri("http://127.0.0.1/chat"), "", "vl-model", http);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CompleteAsync("system", "question", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task StreamAsync_ParsesSseDeltasInOrder()
    {
        var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
        }));
        using var client = new OpenAiCompatibleChatClient(new Uri("http://127.0.0.1/chat"), "", "vl-model", new HttpClient(handler));

        var chunks = new List<string>();
        await foreach (var chunk in client.StreamAsync("system", "question")) chunks.Add(chunk);

        Assert.Equal(["你", "好"], chunks);
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _callback;
        public HttpRequestMessage? LastRequest { get; private set; }
        public DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : this((request, _) => callback(request)) { }
        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) => _callback = callback;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return _callback(request, cancellationToken);
        }
    }
}
