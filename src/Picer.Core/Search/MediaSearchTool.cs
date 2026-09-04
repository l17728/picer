using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Video;

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
    bool AutoRelax = true,
    bool IncludeMissing = false,
    int Limit = 200,
    SortDirection SortDirection = SortDirection.Descending);

public sealed record MediaSearchHit(long Id, string Path, MediaTypeFilter MediaType,
    DateTime? TakenUtc, double? TextScore = null, IReadOnlyList<string>? MatchedFields = null);

public sealed record MediaSearchResult(IReadOnlyList<MediaSearchHit> Items, int TotalCandidates,
    MediaSearchRequest AppliedRequest, IReadOnlyList<string> Warnings);

/// <summary>Read-only, validated search boundary for AI and voice callers.</summary>
public sealed class MediaSearchTool(PicerDatabase database, VideoRepository? videoRepository = null)
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
        var photos = ExecutePhotoPass(filters, terms, request.RequireAllTerms);
        if (!photos.Any() && request.AutoRelax && request.Location is not null)
        {
            // GPS is a soft recall constraint for legacy records: retain person/date/time,
            // remove only geographic bounds, and disclose the relaxation to the caller.
            photos = ExecutePhotoPass(CloneFilters(filters, null, null), terms, request.RequireAllTerms);
            if (photos.Any()) warnings.Add("未找到同时满足 GPS 范围的记录，已回退到地点文字索引。");
        }
        if (people.Length > 0)
        {
            var allowed = new HashSet<long>();
            foreach (var personId in people)
            {
                foreach (var image in IndexSearchEngine.Search(database, CloneFilters(filters, "", personId))) allowed.Add(image.Id);
            }
            photos = photos.Where(image => allowed.Contains(image.Id));
        }
        var totalCandidates = photos.Count();
        var photoHits = request.MediaType == MediaTypeFilter.Video ? [] : photos.Take(request.Limit).Select(image => new MediaSearchHit(image.Id, image.Path,
            MediaTypeFilter.Photo, image.TakenUtc, null, terms.Length == 0 ? [] : ["text"])).ToArray();
        var videoHits = Array.Empty<MediaSearchHit>();
        if (request.MediaType is MediaTypeFilter.Video or MediaTypeFilter.All && videoRepository is not null)
        {
            IEnumerable<VideoRecord> videos = terms.Length == 0 ? videoRepository.GetAll() : terms.SelectMany(videoRepository.Search).DistinctBy(v => v.Id);
            videos = videos.Where(v => request.IncludeMissing || v.Status == "active");
            if (request.StartDate is not null) videos = videos.Where(v => v.TakenUtc is null || v.TakenUtc >= request.StartDate);
            if (request.EndDate is not null) videos = videos.Where(v => v.TakenUtc is null || v.TakenUtc <= request.EndDate);
            if (request.TimeOfDay != TimeOfDayFilter.Any) videos = videos.Where(v => v.TakenUtc is not null &&
                (request.TimeOfDay == TimeOfDayFilter.Day ? v.TakenUtc.Value.ToLocalTime().Hour is >= 6 and < 18 : v.TakenUtc.Value.ToLocalTime().Hour is < 6 or >= 18));
            videoHits = videos.Take(request.Limit).Select(v => new MediaSearchHit(v.Id, v.Path, MediaTypeFilter.Video, v.TakenUtc, null, terms.Length == 0 ? [] : ["text"])).ToArray();
        }
        var hits = request.MediaType == MediaTypeFilter.Video ? videoHits : request.MediaType == MediaTypeFilter.All ? photoHits.Concat(videoHits).Take(request.Limit).ToArray() : photoHits;
        return new(hits, totalCandidates + videoHits.Length, request with { Terms = terms }, warnings);
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

    private IEnumerable<ImageRecord> ExecutePhotoPass(SearchFilters filters, IReadOnlyList<string> terms, bool requireAll)
    {
        if (terms.Count == 0) return IndexSearchEngine.Search(database, filters);
        var sets = terms.Select(term => (IReadOnlyCollection<ImageRecord>)IndexSearchEngine.Search(database, CloneFilters(filters, term, filters.PersonId))).ToArray();
        if (requireAll)
        {
            var common = sets[0].Select(image => image.Id).ToHashSet();
            foreach (var set in sets.Skip(1)) common.IntersectWith(set.Select(image => image.Id));
            return sets[0].Where(image => common.Contains(image.Id));
        }
        return sets.SelectMany(value => value).DistinctBy(image => image.Id);
    }

    private static SearchFilters CloneFilters(SearchFilters source, string? query, long? personId)
    {
        return new SearchFilters
        {
            Query = query ?? "", SourceId = source.SourceId, IncludeMissing = source.IncludeMissing,
            TakenAfter = source.TakenAfter, TakenBefore = source.TakenBefore, TimeOfDay = source.TimeOfDay,
            MinWidth = source.MinWidth, MaxWidth = source.MaxWidth, MinHeight = source.MinHeight, MaxHeight = source.MaxHeight,
            MinFileSize = source.MinFileSize, MaxFileSize = source.MaxFileSize, CameraBrand = source.CameraBrand,
            TagId = source.TagId, GeoBounds = source.GeoBounds, PersonId = personId ?? source.PersonId,
            VisualConceptIds = source.VisualConceptIds, SortBy = source.SortBy, SortDir = source.SortDir
        };
    }
}
