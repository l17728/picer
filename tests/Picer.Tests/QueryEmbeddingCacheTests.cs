using Picer.Core.SemanticSearch;

namespace Picer.Tests;

public sealed class QueryEmbeddingCacheTests
{
    [Fact]
    public void CacheIsBoundedAndSeparatesModelAndAuthorizationScope()
    {
        var cache = new QueryEmbeddingCache(2);
        float[] input = [1, 0];
        cache.Store("first-grant", "model-a", "fixture", input);
        input[0] = 99;
        Assert.Equal(1, cache.Read("first-grant", "model-a", "fixture")![0]);
        Assert.Null(cache.Read("second-grant", "model-a", "fixture"));
        Assert.Null(cache.Read("first-grant", "model-b", "fixture"));
        cache.Store("first-grant", "model-a", "second", [0, 1]);
        cache.Store("first-grant", "model-a", "third", [1, 1]);
        Assert.Null(cache.Read("first-grant", "model-a", "fixture"));
        Assert.NotNull(cache.Read("first-grant", "model-a", "second"));
    }
}
