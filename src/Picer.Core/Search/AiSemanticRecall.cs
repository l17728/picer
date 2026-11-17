using Picer.Core.Data;
using Picer.Core.SemanticSearch;

namespace Picer.Core.Search;

public sealed record AiSemanticRecallResult(IReadOnlyList<long> ImageIds, string Status);

public interface IAiSemanticRecall
{
    Task<AiSemanticRecallResult> SearchAsync(string query, IReadOnlySet<long> allowed, CancellationToken token);
}

/// <summary>Read-only optional recall. Building image vectors is never a query side effect.</summary>
public sealed class AiSemanticRecall(PicerDatabase database, Func<IClipEmbeddingClient?> createClient,
    TimeSpan? timeout = null) : IAiSemanticRecall
{
    private static readonly QueryEmbeddingCache QueryCache = new();
    public async Task<AiSemanticRecallResult> SearchAsync(string query, IReadOnlySet<long> allowed, CancellationToken token)
    {
        if (allowed.Count == 0) return new([], "当前明确条件下没有语义候选。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
        try
        {
            using var read = database.OpenReadOnlyConnection();
            var repository = new ClipSemanticRepository(read);
            if (!repository.HasCurrentVectors(ChineseClipEncoder.ModelVersion, allowed))
                return new([], "当前筛选范围尚无可用的中文语义索引，已使用其他检索通道。");
            var client = createClient();
            if (client is null) return new([], "中文语义服务尚未配置，已使用其他检索通道。");
            using var owned = client as IDisposable;
            var vector = client.CacheScope is { } scope ? QueryCache.Read(scope, client.ModelVersion, query) : null;
            if (vector is null)
            {
                vector = await client.EncodeTextAsync(query, deadline.Token).ConfigureAwait(false);
                if (client.CacheScope is { } cacheScope) QueryCache.Store(cacheScope, client.ModelVersion, query, vector);
            }
            var report = await Task.Run(() => repository.SearchStreaming(vector, 200, client.ModelVersion, allowed,
                cancellationToken: deadline.Token), deadline.Token).ConfigureAwait(false);
            return new(report.Hits.Select(hit => hit.Image.Id).ToArray(), report.Eligible > 0
                ? $"已融合中文语义候选 {report.Hits.Count} 项；相似性不是人物或地点证据。"
                : "模型版本对应的语义索引不可用，已使用其他检索通道。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new([], "中文语义检索超时，已保留其他通道结果。"); }
        catch (HttpRequestException exception)
        {
            return new([], exception.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "当前设备未获语义推理授权，未执行语义检索。" : "中文语义服务暂不可用，已保留其他通道结果。");
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
        { return new([], "中文语义服务返回无效数据，已保留其他通道结果。"); }
    }
}
