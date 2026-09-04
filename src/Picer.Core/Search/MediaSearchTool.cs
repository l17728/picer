using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.Search;

public enum MediaTypeFilter { Photo, Video, All }

public sealed record MediaSearchRequest(
    MediaTypeFilter MediaType = MediaTypeFilter.Photo,
    IReadOnlyList<long>? PersonIds = null,
    GeoBounds? Location = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    TimeOfDayFilter TimeOfDay = TimeOfDayFilter.Any,
    IReadOnlyList<long>? TagIds = null,
    IReadOnlyList<string>? VisualConceptIds = null,
    IReadOnlyList<string>? Terms = null,
    bool RequireAllTerms = false,
    bool IncludeMissing = false,
    int Limit = 200,
    SortDirection SortDirection = SortDirection.Descending);

public sealed record MediaSearchHit(long Id, string Path, MediaTypeFilter MediaType,
    DateTime? TakenUtc, double? TextScore = null, IReadOnlyList<string>? MatchedFields = null);

public sealed record MediaSearchResult(IReadOnlyList<MediaSearchHit> Items, int TotalCandidates,
    MediaSearchRequest AppliedRequest, IReadOnlyList<string> Warnings);

/// <summary>Read-only, validated search boundary for AI and voice callers.</summary>
public sealed class MediaSearchTool(PicerDatabase database)
{
    public MediaSearchResult Execute(MediaSearchRequest request)
    {
        Validate(request);
        var warnings = new List<string>();
        var terms = request.Terms?.Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => term.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        if (request.RequireAllTerms && terms.Length > 1)
            warnings.Add("当前文字索引按候选召回后复核，多个词项的全部满足由复核阶段确认。");

        var filters = new SearchFilters
        {
            GeoBounds = request.Location,
            TakenAfter = request.StartDate,
            TakenBefore = request.EndDate,
            TimeOfDay = request.TimeOfDay,
            IncludeMissing = request.IncludeMissing,
            SortBy = SortField.TakenDate,
            SortDir = request.SortDirection
        };
        var people = request.PersonIds?.Where(id => id > 0).Distinct().ToArray() ?? [];
        // SearchFilters currently accepts one person ID; intersect additional
        // people in memory without weakening the query boundary.
        var candidateSets = new List<IReadOnlyList<ImageRecord>>();
        if (terms.Length == 0)
            candidateSets.Add(IndexSearchEngine.Search(database, filters));
        else
            foreach (var term in terms)
            {
                filters.Query = term;
                candidateSets.Add(IndexSearchEngine.Search(database, filters));
            }
        var photos = candidateSets.SelectMany(value => value).DistinctBy(image => image.Id);
        if (people.Length > 0)
        {
            var allowed = new HashSet<long>();
            foreach (var personId in people)
            {
                filters.PersonId = personId;
                foreach (var image in IndexSearchEngine.Search(database, filters)) allowed.Add(image.Id);
            }
            photos = photos.Where(image => allowed.Contains(image.Id));
        }
        var hits = photos.Take(request.Limit).Select(image => new MediaSearchHit(image.Id, image.Path,
            MediaTypeFilter.Photo, image.TakenUtc, null, terms.Length == 0 ? [] : ["text"])).ToArray();
        if (request.MediaType == MediaTypeFilter.Video)
            hits = [];
        return new(hits, hits.Length, request with { Terms = terms }, warnings);
    }

    private static void Validate(MediaSearchRequest request)
    {
        if (!Enum.IsDefined(request.MediaType)) throw new ArgumentOutOfRangeException(nameof(request.MediaType));
        if (!Enum.IsDefined(request.TimeOfDay)) throw new ArgumentOutOfRangeException(nameof(request.TimeOfDay));
        if (request.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(request.Limit));
        if (request.StartDate is not null && request.EndDate is not null && request.StartDate > request.EndDate)
            throw new ArgumentException("开始时间不能晚于结束时间。", nameof(request));
        if (request.PersonIds?.Any(id => id <= 0) == true) throw new ArgumentException("人物 ID 必须为正数。", nameof(request));
        if (request.TagIds?.Any(id => id <= 0) == true) throw new ArgumentException("标签 ID 必须为正数。", nameof(request));
    }
}
