using System.Net;
using System.Text.Json;
using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class MetadataStreamingTests
{
    [Fact]
    public async Task MetadataBodyCanOutliveAccountRequestTimeoutWithoutBuffering()
    {
        using var handler = new MetadataHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://metadata.test/"), Timeout = TimeSpan.FromMilliseconds(200) };
        using var gateway = new HttpIndexGateway(client);
        var metadata = await gateway.GetMetadataAsync(CancellationToken.None, includeEmbeddings: true, includeFaceModels: true);
        Assert.Empty(metadata.Faces);
        Assert.False(handler.Buffered);
        Assert.True(handler.ContentDisposed);
    }

    [Fact]
    public async Task MetadataBodyRemainsCancelableAndDisposesItsResponse()
    {
        using var handler = new MetadataHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://metadata.test/") };
        using var gateway = new HttpIndexGateway(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.GetMetadataAsync(cancellation.Token, true, true));
        Assert.False(handler.Buffered);
        Assert.True(handler.ContentDisposed);
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        public bool Buffered { get; set; }
        public bool ContentDisposed { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new MetadataContent(this) });
    }

    private sealed class MetadataContent : HttpContent
    {
        private readonly MetadataHandler _owner;
        private readonly byte[] _bytes = JsonSerializer.SerializeToUtf8Bytes(new GatewayMetadata([], [], []), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        public MetadataContent(MetadataHandler owner)
        {
            _owner = owner;
            Headers.ContentType = new("application/json");
        }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            _owner.Buffered = true;
            await Task.Delay(600);
            await stream.WriteAsync(_bytes);
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new SlowStream(_bytes));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) _owner.ContentDisposed = true; base.Dispose(disposing); }
    }

    private sealed class SlowStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        private bool _first = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first) { _first = false; await Task.Delay(600, cancellationToken); }
            return await _inner.ReadAsync(buffer, cancellationToken);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
