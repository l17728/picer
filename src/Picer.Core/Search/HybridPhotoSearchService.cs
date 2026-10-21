using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.SemanticSearch;

namespace Picer.Core.Search;

public sealed record HybridPhotoSearchHit(ImageRecord Image, double RrfScore, int? TextRank, int? ClipRank,
    IReadOnlyList<string> MatchSources);

public sealed record HybridPhotoSearchResult(IReadOnlyList<HybridPhotoSearchHit> Hits, bool ClipUsed,
    int ClipIndexed, int ClipEligible, IReadOnlyList<string> Warnings);

public sealed class HybridPhotoSearchService(PicerDatabase database, ClipSemanticRepository clipRepository,
    IClipEmbeddingClient embeddings)
{
    public Task<HybridPhotoSearchResult> SearchAsync(string query, SearchFilters? hardFilters = null,
        int count = 20, int candidateCount = 200, int rankConstant = 60,
        CancellationToken cancellationToken = default) => SearchCoreAsync(query, hardFilters, count,
            candidateCount, rankConstant, cancellationToken);

    private async Task<HybridPhotoSearchResult> SearchCoreAsync(string query, SearchFilters? hardFilters,
        int count, int candidateCount, int rankConstant, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return new HybridPhotoSearchResult([], false, 0, 0, ["请输入检索文字。"]);
        var filters = CopyWithoutQuery(hardFilters);
        var allowed = IndexSearchEngine.Search(database, filters).Select(image => image.Id).ToHashSet();
        cancellationToken.ThrowIfCancellationRequested();

        var text = new MediaTextRanking(database).Search([query]).Where(item => allowed.Contains(item.Key))
            .OrderByDescending(item => item.Value).ThenBy(item => item.Key).Take(candidateCount)
            .Select((item, index) => new RankedCandidate(item.Key, index + 1)).ToArray();
        var queryVector = await embeddings.EncodeTextAsync(query, cancellationToken).ConfigureAwait(false);
        var clip = clipRepository.SearchStreaming(queryVector, candidateCount,
            embeddings.ModelVersion, allowed, cancellationToken: cancellationToken);
        var fused = ReciprocalRankFusion.Fuse([
            ("FTS", (IEnumerable<RankedCandidate>)text),
            ("CLIP", clip.Hits.Select(hit => new RankedCandidate(hit.Image.Id, hit.Rank)))
        ], count, rankConstant, cancellationToken);
        var hits = fused.Select(hit =>
        {
            var image = database.GetImage(hit.MediaId)!;
            hit.SourceRanks.TryGetValue("FTS", out var textRank);
            hit.SourceRanks.TryGetValue("CLIP", out var clipRank);
            return new HybridPhotoSearchHit(image, hit.Score, textRank == 0 ? null : textRank,
                clipRank == 0 ? null : clipRank, hit.SourceRanks.Keys.Order().ToArray());
        }).ToArray();
        var warnings = new List<string>();
        if (clip.Eligible == 0) warnings.Add("当前筛选范围没有可用的 CLIP 向量，结果已回退为文字检索。");
        if (clip.InvalidVectors > 0) warnings.Add($"跳过了 {clip.InvalidVectors} 条模型版本或维度不一致的向量。");
        return new HybridPhotoSearchResult(hits, clip.Eligible > 0, clip.Indexed, clip.Eligible, warnings);
    }

    private static SearchFilters CopyWithoutQuery(SearchFilters? source) => source is null ? new SearchFilters() : new SearchFilters
    {
        SourceId = source.SourceId,
        IncludeMissing = source.IncludeMissing,
        TakenAfter = source.TakenAfter,
        TakenBefore = source.TakenBefore,
        TimeOfDay = source.TimeOfDay,
        MinWidth = source.MinWidth,
        MaxWidth = source.MaxWidth,
        MinHeight = source.MinHeight,
        MaxHeight = source.MaxHeight,
        MinFileSize = source.MinFileSize,
        MaxFileSize = source.MaxFileSize,
        CameraBrand = source.CameraBrand,
        TagId = source.TagId,
        GeoBounds = source.GeoBounds,
        PersonId = source.PersonId,
        VisualConceptIds = source.VisualConceptIds.ToArray(),
        IncludeSuggestedClassifications = source.IncludeSuggestedClassifications,
        SortBy = source.SortBy,
        SortDir = source.SortDir
    };
}
