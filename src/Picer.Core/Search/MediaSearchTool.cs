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
    SortDirection SortDirection = SortDirection.Descending,
    bool IncludeSuggestedClassifications = false);

public sealed record MediaSearchHit(long Id, string Path, MediaTypeFilter MediaType,
    DateTime? TakenUtc, double? TextScore = null, IReadOnlyList<string>? MatchedFields = null);

public sealed record MediaSearchResult(IReadOnlyList<MediaSearchHit> Items, int TotalCandidates,
    MediaSearchRequest AppliedRequest, IReadOnlyList<string> Warnings);

/// <summary>Read-only, validated search boundary for AI and voice callers.</summary>
public sealed class MediaSearchTool(PicerDatabase database, VideoRepository? videoRepository = null)
{
    /// <summary>Common paged adapter for the ordinary library UI.</summary>
    public Task<SearchPage> ExecutePagedAsync(SearchFilters filters, int pageSize = 200,
        SearchCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return IndexSearchEngine.SearchPagedAsync(database, filters, pageSize, cursor, cancellationToken);
    }

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
            SortDir = request.SortDirection,
            VisualConceptIds = request.VisualConceptIds ?? [],
            IncludeSuggestedClassifications = request.IncludeSuggestedClassifications
        };
        var people = request.PersonIds?.Where(id => id > 0).Distinct().ToArray() ?? [];
        // SearchFilters currently accepts one person ID; intersect additional
        // people in memory without weakening the query boundary.
        var photos = ExecutePhotoPass(filters, terms, request.RequireAllTerms);
        var textScores = new MediaTextRanking(database).Search(terms, request.RequireAllTerms);
        if (textScores.Count > 0)
        {
            var structured = IndexSearchEngine.Search(database, filters);
            photos = photos.Concat(structured.Where(image => textScores.ContainsKey(image.Id))).DistinctBy(image => image.Id);
        }
        var relaxedLocation = false;
        if (!photos.Any() && request.AutoRelax && request.Location is not null)
        {
            // GPS is a soft recall constraint for legacy records: retain person/date/time,
            // remove only geographic bounds, and disclose the relaxation to the caller.
            var relaxedFilters = CloneFilters(filters, null, null);
            relaxedFilters.GeoBounds = null;
            photos = ExecutePhotoPass(relaxedFilters, terms, request.RequireAllTerms);
            relaxedLocation = photos.Any();
            if (photos.Any()) warnings.Add("未找到同时满足 GPS 范围的记录，已回退到地点文字索引。");
        }
        if (people.Length > 0)
        {
            HashSet<long>? allowed = null;
            foreach (var personId in people)
            {
                var personFilters = CloneFilters(filters, "", personId);
                if (relaxedLocation) personFilters.GeoBounds = null;
                var personImages = IndexSearchEngine.Search(database, personFilters).Select(image => image.Id).ToHashSet();
                if (allowed is null) allowed = personImages; else allowed.IntersectWith(personImages);
            }
            photos = photos.Where(image => allowed!.Contains(image.Id));
        }
        if (request.TagIds is { Count: > 0 })
        {
            var tagged = GetImagesWithTags(request.TagIds.Distinct().ToArray());
            photos = photos.Where(image => tagged.Contains(image.Id));
        }
        var totalCandidates = request.MediaType == MediaTypeFilter.Video ? 0 : photos.Count();
        photos = photos.OrderByDescending(image => textScores.GetValueOrDefault(image.Id))
            .ThenBy(image => request.SortDirection == SortDirection.Ascending ? (image.TakenUtc ?? image.ModifiedUtc).Ticks : -(image.TakenUtc ?? image.ModifiedUtc).Ticks)
            .ThenBy(image => image.Id);
        var videoCandidates = 0;
        var photoHits = request.MediaType == MediaTypeFilter.Video ? [] : photos.Take(request.Limit).Select(image => new MediaSearchHit(image.Id, image.Path,
            MediaTypeFilter.Photo, image.TakenUtc, textScores.GetValueOrDefault(image.Id), terms.Length == 0 ? [] : ["text"])).ToArray();
        var videoHits = Array.Empty<MediaSearchHit>();
        if (request.MediaType is MediaTypeFilter.Video or MediaTypeFilter.All && videoRepository is not null)
        {
            IEnumerable<VideoRecord> videos = terms.Length == 0 ? videoRepository.GetAll() :
                request.RequireAllTerms
                    ? videoRepository.GetAll().Where(v => terms.All(term => videoRepository.Search(term).Any(hit => hit.Id == v.Id)))
                    : terms.SelectMany(videoRepository.Search).DistinctBy(v => v.Id);
            if (terms.Length > 0)
                videos = videos.Concat(videoRepository.GetAll().Where(video => textScores.ContainsKey(-video.Id))).DistinctBy(video => video.Id);
            var allowedVideos = new VideoSearchMetadata(database).Match(people, request.TagIds ?? [], request.VisualConceptIds ?? []);
            videos = videos.Where(video => allowedVideos.Contains(video.Id) && VideoSearchMetadata.Within(video, request.Location));
            videos = videos.Where(v => request.IncludeMissing || v.Status == "active");
            if (request.StartDate is not null) videos = videos.Where(v => v.TakenUtc is not null && v.TakenUtc >= request.StartDate);
            if (request.EndDate is not null) videos = videos.Where(v => v.TakenUtc is not null && v.TakenUtc <= request.EndDate);
            if (request.TimeOfDay != TimeOfDayFilter.Any) videos = videos.Where(v => v.TakenUtc is not null &&
                (request.TimeOfDay == TimeOfDayFilter.Day ? v.TakenUtc.Value.ToLocalTime().Hour is >= 6 and < 18 : v.TakenUtc.Value.ToLocalTime().Hour is < 6 or >= 18));
            videoCandidates = videos.Count();
            videoHits = videos.OrderByDescending(v => textScores.GetValueOrDefault(-v.Id))
                .ThenBy(v => request.SortDirection == SortDirection.Ascending ? (v.TakenUtc ?? v.ModifiedUtc).Ticks : -(v.TakenUtc ?? v.ModifiedUtc).Ticks)
                .ThenBy(v => v.Id).Take(request.Limit).Select(v => new MediaSearchHit(v.Id, v.Path, MediaTypeFilter.Video, v.TakenUtc,
                    textScores.GetValueOrDefault(-v.Id), terms.Length == 0 ? [] : ["text"])).ToArray();
        }
        var hits = request.MediaType == MediaTypeFilter.Video ? videoHits : request.MediaType == MediaTypeFilter.All
            ? photoHits.Concat(videoHits).OrderByDescending(hit => hit.TextScore ?? 0).ThenBy(hit => request.SortDirection == SortDirection.Ascending ? hit.TakenUtc?.Ticks : -hit.TakenUtc?.Ticks).Take(request.Limit).ToArray() : photoHits;
        return new(hits, totalCandidates + videoCandidates, request with { Terms = terms }, warnings);
    }

    private static void Validate(MediaSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.VisualConceptIds?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("视觉概念 ID 不能为空。", nameof(request));
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
            VisualConceptIds = source.VisualConceptIds, IncludeSuggestedClassifications = source.IncludeSuggestedClassifications,
            SortBy = source.SortBy, SortDir = source.SortDir
        };
    }

    private HashSet<long> GetImagesWithTags(IReadOnlyList<long> tagIds)
    {
        using var command = database.Command($"SELECT image_id FROM image_tags WHERE tag_id IN ({string.Join(',', tagIds.Select((_, i) => "$tag" + i))}) GROUP BY image_id HAVING COUNT(DISTINCT tag_id) = $tagCount");
        for (var i = 0; i < tagIds.Count; i++) command.Parameters.AddWithValue("$tag" + i, tagIds[i]);
        command.Parameters.AddWithValue("$tagCount", tagIds.Count);
        using var reader = command.ExecuteReader();
        var result = new HashSet<long>();
        while (reader.Read()) result.Add(reader.GetInt64(0));
        return result;
    }
}
