using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class MetadataTransferTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientSupportsDeltaAndLegacyResponsesAndSeparatesOptions(bool supportsDelta)
    {
        using var handler=new MetadataHandler(supportsDelta);
        using var client=new HttpClient(handler) { BaseAddress=new Uri("https://fixture.invalid/") };
        using var gateway=new HttpIndexGateway(client);
        Assert.Single((await gateway.GetMetadataAsync()).FaceReviewStatesOrEmpty);
        Assert.Single((await gateway.GetMetadataAsync()).FaceReviewStatesOrEmpty);
        if(supportsDelta) Assert.Equal(0,handler.LastChanges);
        await gateway.GetMetadataAsync(CancellationToken.None,true,true);
        Assert.Equal(0,handler.LastSuppliedHashes); // Different options need a separate baseline.
        handler.Metadata=handler.Metadata with { FaceReviewStates=[] };
        Assert.Empty((await gateway.GetMetadataAsync()).FaceReviewStatesOrEmpty);
    }

    private sealed class MetadataHandler(bool delta) : HttpMessageHandler
    {
        public GatewayMetadata Metadata=new([],[],[]) { FaceReviewStates=[new(1,"ignored","time")] };
        public int LastChanges,LastSuppliedHashes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var header=Assert.Single(request.Headers.GetValues(MetadataTransfer.RequestHeader));
            LastSuppliedHashes=JsonSerializer.Deserialize<Dictionary<string,string>>(Convert.FromBase64String(header))!.Count;
            var response=new HttpResponseMessage(HttpStatusCode.OK);
            if(delta)
            {
                var transfer=MetadataTransfer.Create(Metadata,header);LastChanges=transfer.Changes.Count;
                response.Headers.Add(MetadataTransfer.ResponseHeader,"1");response.Content=JsonContent.Create(transfer);
            }
            else response.Content=JsonContent.Create(Metadata);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void UnchangedSnapshotTransfersNoCollectionsAndDeletionTransfersEmptyCollection()
    {
        var original = new GatewayMetadata([],[],[]) with { FaceReviewStates=[new(1,"ignored","time")] };
        var full = MetadataTransfer.Create(original, "");
        full.Apply(null, out var baseline);
        var header = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(full.Hashes));
        var unchanged = MetadataTransfer.Create(original, header);
        Assert.Empty(unchanged.Changes);
        Assert.Single(unchanged.Apply(baseline, out _).FaceReviewStatesOrEmpty);
        var deletion = MetadataTransfer.Create(original with { FaceReviewStates=[] }, header);
        Assert.NotEmpty(deletion.Changes);
        Assert.Empty(deletion.Apply(baseline, out _).FaceReviewStatesOrEmpty);
        Assert.Single(original.FaceReviewStatesOrEmpty);
    }

    [Fact]
    public void MissingBaselineIsRejectedAndInvalidHeaderForcesCompletePayload()
    {
        var metadata = new GatewayMetadata([],[],[]);
        var full = MetadataTransfer.Create(metadata,"bad-base64");
        full.Apply(null,out var baseline);
        var delta = MetadataTransfer.Create(metadata,Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(full.Hashes)));
        Assert.Throws<InvalidDataException>(()=>delta.Apply(null,out _));
        baseline.Remove(full.Hashes.Keys.First());
        Assert.Throws<InvalidDataException>(()=>delta.Apply(baseline,out _));
    }

    [Fact]
    public void NullOptionalCollectionRemainsDistinctFromEmptyAndBaselineIsNotMutated()
    {
        var metadata = new GatewayMetadata([],[],[]) with { FaceReviewStates=[] };
        var full = MetadataTransfer.Create(metadata,""); full.Apply(null,out var baseline);
        var before=baseline.ToJsonString();
        var delta=MetadataTransfer.Create(metadata with { FaceReviewStates=null },Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(full.Hashes)));
        Assert.Null(delta.Apply(baseline,out _).FaceReviewStates);
        Assert.Equal(before,baseline.ToJsonString());
    }
}
