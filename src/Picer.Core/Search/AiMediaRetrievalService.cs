using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Video;
using Picer.Core.Vision;
using Picer.Core.Logging;
using System.Diagnostics;

namespace Picer.Core.Search;

public sealed record AiQueryPlan(string? PersonName, string? Location, string? StartDate, string? EndDate,
    IReadOnlyList<string> Keywords, string Intent, IReadOnlyList<string>? LocationTerms = null);
public sealed record AiMediaAnswer(string Markdown, IReadOnlyList<ImageRecord> Photos,
    IReadOnlyList<VideoRecord> Videos, AiQueryPlan Plan);

public sealed class AiMediaRetrievalService(PicerDatabase database, PeopleService people,
    VideoRepository videos, CloudVisionRepository cloud, OpenAiCompatibleChatClient chat)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<AiMediaAnswer> AskAsync(string question, bool includeVideos, DateTime today,
        IProgress<(string Stage, int Percent)>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(("理解问题", 5));
        // Only expose manually confirmed identities to the planner; candidate
        // and unassigned clusters are not valid person filters.
        var names = people.GetAll().Where(value => value.ConfirmedFaceCount > 0)
            .Select(value => value.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var planText = await chat.CompleteAsync("""
            Output JSON field locationTerms as an array of alternative place names and administrative expressions for recall (for example Shanghai, Shanghai City). Do not use these terms as final proof; they are only candidate retrieval hints.
            你是私人媒体库的查询规划器。只输出JSON：personName、location、startDate、endDate、keywords、intent。
            日期必须为yyyy-MM-dd；keywords是字符串数组，只保留人物/地点/日期之外的服装、颜色、活动、物体和场景词。
            不得虚构人物名称。无法确定的字段为null。
            """, $"今天：{today:yyyy-MM-dd}\n已命名人物：{string.Join('、', names)}\n是否包含视频：{includeVideos}\n问题：{question}",
            450, cancellationToken).ConfigureAwait(false);
        AiQueryPlan plan;
        try { plan = JsonSerializer.Deserialize<AiQueryPlan>(OpenAiCompatibleChatClient.StripFence(planText), Json)!; }
        catch (Exception exception) { throw new InvalidDataException("AI 未能生成有效的结构化查询。", exception); }
        if (plan is null) throw new InvalidDataException("AI 查询计划为空。");
        plan = plan with { Keywords = plan.Keywords?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct().ToArray() ?? [] };
        AppLogger.Info($"AiQuestion plan ready; hasPerson={(!string.IsNullOrWhiteSpace(plan.PersonName))}; hasLocation={(!string.IsNullOrWhiteSpace(plan.Location))}; hasDate={(!string.IsNullOrWhiteSpace(plan.StartDate) || !string.IsNullOrWhiteSpace(plan.EndDate))}; keywordCount={plan.Keywords.Count}; elapsedMs={stopwatch.ElapsedMilliseconds}");

        progress?.Report(("检索照片索引", 25));
        // An explicit confirmed name in the user's question is authoritative.
        // Do not let a planner typo or hallucination replace it.
        var questionMention = people.FindMention(question);
        // “我/本人” is a user-facing identity, not a literal free-text
        // keyword. If the user has explicitly created a confirmed person
        // with that name, bind the pronoun before retrieval so the reviewer
        // cannot discard otherwise valid photos as unrelated people.
        if (questionMention is null && ContainsSelfReference(question))
        {
            var self = people.FindByName("我") ?? people.FindByName("本人");
            if (self is not null)
                questionMention = new PersonQueryMention(self, "我", question.Replace("我", string.Empty, StringComparison.Ordinal));
        }
        var normalizedPerson = questionMention?.Person.DisplayName ?? NormalizePlannerText(plan.PersonName);
        // A natural-language query may name more than one confirmed person
        // (for example, “我和牟”).  The planner schema intentionally keeps a
        // primary person for compatibility, but retrieval must preserve the
        // full explicit set and require their intersection.
        var allPeople = people.GetAll();
        var explicitPeople = allPeople
            .Where(value => value.ConfirmedFaceCount > 0 &&
                question.Contains(value.DisplayName, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(value => value.Id)
            .ToArray();
        if (questionMention is null && ContainsSelfReference(question))
        {
            var self = people.FindByName("我") ?? people.FindByName("本人");
            if (self is not null && allPeople.FirstOrDefault(value => value.Id == self.Id) is { ConfirmedFaceCount: > 0 } selfSummary &&
                explicitPeople.All(value => value.Id != selfSummary.Id))
                explicitPeople = [selfSummary, .. explicitPeople];
        }
        var normalizedLocation = NormalizeLocation(plan.Location);
        plan = plan with { PersonName = normalizedPerson, Location = normalizedLocation };
        var locationTerms = (plan.LocationTerms ?? [])
            .Concat(normalizedLocation is null ? [] : [normalizedLocation])
            .Concat(ExpandKnownLocationAliases(normalizedLocation))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var mention = questionMention?.Person ?? (!string.IsNullOrWhiteSpace(normalizedPerson)
            ? people.FindByName(normalizedPerson) ??
              (people.GetAll().FirstOrDefault(person =>
                  string.Equals(NormalizePlannerText(person.DisplayName), normalizedPerson, StringComparison.OrdinalIgnoreCase)) is { } summary
                  ? people.Get(summary.Id)
                  : null)
            : null);
        if (questionMention is not null)
            AppLogger.Info($"AiQuestion person constraint pinned to explicit mention; aliasLength={questionMention.MatchedName.Length}");
        var bounds = string.IsNullOrWhiteSpace(normalizedLocation) ? null : GeoPlaceResolver.Resolve(normalizedLocation);
        var requiredPeople = explicitPeople.Length > 0
            ? explicitPeople
            : mention is null
                ? Array.Empty<PersonSummary>()
                : allPeople.Where(value => value.Id == mention.Id).ToArray();
        AppLogger.Info($"AiQuestion constraints normalized; personBound={mention is not null}; personConstraintCount={requiredPeople.Length}; locationResolved={bounds is not null}; locationTermCount={locationTerms.Length}; selfReference={ContainsSelfReference(question)}");
        DateTime? start = DateTime.TryParse(plan.StartDate, out var startValue) ? startValue.Date.ToUniversalTime() : null;
        DateTime? end = DateTime.TryParse(plan.EndDate, out var endValue) ? endValue.Date.AddDays(1).AddTicks(-1).ToUniversalTime() : null;
        var dayPart = ResolveDayPart(question, plan.Keywords);
        var baseFilter = new SearchFilters { PersonId = mention?.Id, GeoBounds = bounds, TakenAfter = start,
            TakenBefore = end, IncludeMissing = false, SortBy = SortField.TakenDate, SortDir = SortDirection.Descending };
        var photoCandidates = new Dictionary<long, ImageRecord>();
        // Time-of-day words are structured constraints, not text that must occur
        // in a description. Searching for "白天" literally used to discard all
        // otherwise matching photos because descriptions usually contain a clock
        // time (or no time phrase at all).
        baseFilter.TimeOfDay = dayPart switch { DayPart.Day => TimeOfDayFilter.Day, DayPart.Night => TimeOfDayFilter.Night, _ => TimeOfDayFilter.Any };
        var searchTerms = QueryRewrite.Expand(plan.Keywords.Where(term => !IsDayPartToken(term)))
            // Keep the literal location as a text fallback even when geocoding
            // returned bounds; older analyses may have place names but no GPS.
            .Concat(QueryRewrite.Expand(locationTerms))
            .ToArray();
        var toolResult = new MediaSearchTool(database, videos).Execute(new MediaSearchRequest(
            MediaTypeFilter.Photo, mention is null ? null : [mention.Id], bounds, start, end,
            baseFilter.TimeOfDay, Terms: searchTerms, IncludeMissing: false, Limit: 200,
            SortDirection: SortDirection.Descending));
        foreach (var hit in toolResult.Items)
        {
            var image = database.GetImage(hit.Id);
            if (image is not null && HasAllRequiredPeople(image.Id, requiredPeople)) photoCandidates[image.Id] = image;
        }
        AppLogger.Info($"AiQuestion photo retrieval completed; primaryHits={toolResult.Items.Count}; uniquePhotos={photoCandidates.Count}; geoFilter={(bounds is not null)}; elapsedMs={stopwatch.ElapsedMilliseconds}");
        if (bounds is not null && locationTerms.Length > 0)
        {
            // GPS is a candidate source, not a hard semantic decision. A second
            // text-only pass keeps records whose descriptions say “上海” while
            // GPS is missing, coarse, or outside an imperfect city rectangle.
            var textFallback = new MediaSearchTool(database).Execute(new MediaSearchRequest(
                MediaTypeFilter.Photo, mention is null ? null : [mention.Id], null, start, end,
                baseFilter.TimeOfDay, Terms: searchTerms, IncludeMissing: false, Limit: 200,
                SortDirection: SortDirection.Descending));
            foreach (var hit in textFallback.Items)
            {
                var image = database.GetImage(hit.Id);
                if (image is not null && HasAllRequiredPeople(image.Id, requiredPeople)) photoCandidates[image.Id] = image;
            }
        }

        progress?.Report(("检索视频索引", 45));
        var videoCandidates = includeVideos ? RetrieveVideos(plan, mention?.Id, start, end).Take(50).ToArray() : [];
        var photos = photoCandidates.Values.Take(50).ToArray();
        if (photos.Length == 0 && videoCandidates.Length == 0)
        {
            progress?.Report(("完成", 100));
            AppLogger.Info($"AiQuestion completed without evidence; photos=0; videos=0; elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new("## 未找到匹配内容\n\n当前索引中没有同时满足这些条件的照片或视频。", [], [], plan);
        }
        progress?.Report(("AI复核候选", 65));
        var evidence = new
        {
            question,
            plan,
            photos = photos.Select(image => new { id = image.Id, file = Path.GetFileName(image.Path),
                time = image.TakenUtc ?? image.CreatedUtc ?? image.ModifiedUtc,
                gps = image.Gps,
                location = GetLocationDisplay(image.Id),
                confirmedPersonNames = people.GetConfirmedPersonNamesForImage(image.Id),
                requiredPersonNames = requiredPeople.Select(value => value.DisplayName).ToArray(),
                description = cloud.GetLatestCompleted(image.Id)?.Result.Summary }).ToArray(),
            videos = videoCandidates.Select(video => new { id = video.Id, file = Path.GetFileName(video.Path),
                time = video.TakenUtc ?? video.ModifiedUtc, durationSeconds = video.DurationSeconds,
                confirmedPersonNames = people.GetConfirmedPersonNamesForVideo(video.Id),
                transcript = Truncate(videos.GetTranscript(video.Id), 500), summary = videos.GetSummary(video.Id) }).ToArray()
        };
        var reviewText = await chat.CompleteAsync("""
            你是私人媒体库检索复核器。只能从给定候选ID中选择，不得编造ID或人物身份。
            confirmedPersonNames 是程序依据人工确认人脸生成的权威字段；问题指定人物时，必须保留该字段包含目标人物的候选，不能把目标人物改写成其他姓名。
            输出JSON：markdown、photoIds、videoIds。markdown使用中文Markdown，说明结论、命中依据和不确定项，照片与视频分节。
            没有可靠结果时明确说明。photoIds和videoIds必须是数字数组。
            """, JsonSerializer.Serialize(evidence), 900, cancellationToken).ConfigureAwait(false);
        Review review;
        try { review = JsonSerializer.Deserialize<Review>(OpenAiCompatibleChatClient.StripFence(reviewText), Json)!; }
        catch { review = new Review($"## 检索结果\n\n找到 {photos.Length} 张候选照片和 {videoCandidates.Length} 个候选视频。", photos.Select(value => value.Id).ToArray(), videoCandidates.Select(value => value.Id).ToArray()); }
        var allowedPhotos = photos.ToDictionary(value => value.Id);
        var allowedVideos = videoCandidates.ToDictionary(value => value.Id);
        var rankedPhotos = (review.PhotoIds ?? []).Where(allowedPhotos.ContainsKey).Distinct().Select(id => allowedPhotos[id]).ToArray();
        var rankedVideos = (review.VideoIds ?? []).Where(allowedVideos.ContainsKey).Distinct().Select(id => allowedVideos[id]).ToArray();
        if (mention is not null && rankedPhotos.Length == 0 && rankedVideos.Length == 0)
        {
            rankedPhotos = photos.Where(image => HasAllRequiredPeople(image.Id, requiredPeople)).ToArray();
            rankedVideos = videoCandidates.Where(video => people.GetConfirmedPersonNamesForVideo(video.Id)
                .Contains(mention.DisplayName, StringComparer.OrdinalIgnoreCase)).ToArray();
            AppLogger.Info($"AiQuestion reviewer fallback applied for confirmed person; photos={rankedPhotos.Length}; videos={rankedVideos.Length}");
        }
        if (mention is not null && (rankedPhotos.Length > 0 || rankedVideos.Length > 0))
            review = review with { Markdown = (review.Markdown ?? "未生成回答") + $"\n\n> 索引确认：以上结果包含人工标记人物“{mention.DisplayName}”的媒体；人物标签来自本地确认记录。" };
        progress?.Report(("完成", 100));
        return new(review.Markdown ?? "未生成回答。", rankedPhotos, rankedVideos, plan);
    }

    private static string? NormalizePlannerText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().Trim(' ', '，', ',', '。', '；', ';', '：', ':', '"', '\'');
    }

    private static string? NormalizeLocation(string? value)
    {
        var normalized = NormalizePlannerText(value);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        foreach (var suffix in new[] { "市", "省", "自治区", "特别行政区" })
            if (normalized.EndsWith(suffix, StringComparison.Ordinal) && normalized.Length > suffix.Length)
                return normalized[..^suffix.Length];
        return normalized;
    }

    private static IEnumerable<string> ExpandKnownLocationAliases(string? location)
    {
        // 商县 is the historical name commonly used for today's 商州区. Keep
        // this bounded fallback only as a recall hint; the LLM reviewer and
        // indexed GPS/location metadata remain authoritative.
        if (string.Equals(location, "商县", StringComparison.OrdinalIgnoreCase))
            return ["商州", "商州区", "商洛", "商洛市"];
        return [];
    }

    private static bool ContainsSelfReference(string question) =>
        question.Contains("我", StringComparison.Ordinal) || question.Contains("本人", StringComparison.Ordinal);

    private IReadOnlyList<VideoRecord> RetrieveVideos(AiQueryPlan plan, long? personId, DateTime? start, DateTime? end)
    {
        var terms = plan.Keywords.Concat(!string.IsNullOrWhiteSpace(plan.Location) ? [plan.Location!] : []).ToArray();
        var candidates = terms.Length == 0 ? videos.GetAll() : terms.SelectMany(videos.Search).DistinctBy(value => value.Id).ToArray();
        HashSet<long>? personVideos = null;
        if (personId is not null)
        {
            using var command = database.Command("SELECT DISTINCT video_id FROM video_detected_faces WHERE assigned_person_id=$person AND assignment_state='confirmed'");
            command.Parameters.AddWithValue("$person", personId.Value); personVideos = [];
            using var reader = command.ExecuteReader(); while (reader.Read()) personVideos.Add(reader.GetInt64(0));
        }
        return candidates.Where(video => video.Status == "active" && (personVideos is null || personVideos.Contains(video.Id)))
            .Where(video => start is null || (video.TakenUtc ?? video.ModifiedUtc) >= start)
            .Where(video => end is null || (video.TakenUtc ?? video.ModifiedUtc) <= end)
            .OrderByDescending(video => video.TakenUtc ?? video.ModifiedUtc).ToArray();
    }

    private static SearchFilters Clone(SearchFilters value) => new() { PersonId = value.PersonId, GeoBounds = value.GeoBounds,
        TakenAfter = value.TakenAfter, TakenBefore = value.TakenBefore, IncludeMissing = value.IncludeMissing,
        TimeOfDay = value.TimeOfDay,
        SortBy = value.SortBy, SortDir = value.SortDir };
    private static string? Truncate(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value[..Math.Min(length, value.Length)];
    private enum DayPart { None, Day, Night }

    private static DayPart ResolveDayPart(string question, IReadOnlyList<string> keywords)
    {
        var text = question + " " + string.Join(' ', keywords);
        if (text.Contains("白天", StringComparison.OrdinalIgnoreCase) || text.Contains("白日", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("白昼", StringComparison.OrdinalIgnoreCase) || text.Contains("白天时段", StringComparison.OrdinalIgnoreCase)) return DayPart.Day;
        if (text.Contains("晚上", StringComparison.OrdinalIgnoreCase) || text.Contains("夜间", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("夜晚", StringComparison.OrdinalIgnoreCase) || text.Contains("深夜", StringComparison.OrdinalIgnoreCase)) return DayPart.Night;
        return DayPart.None;
    }

    private static bool IsDayPartToken(string value) => value.Contains("白天", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("白昼", StringComparison.OrdinalIgnoreCase) || value.Contains("晚上", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("夜间", StringComparison.OrdinalIgnoreCase) || value.Contains("夜晚", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("深夜", StringComparison.OrdinalIgnoreCase);

    private string? GetLocationDisplay(long imageId)
    {
        try
        {
            using var command = database.Command(
                "SELECT display_name FROM image_locations WHERE image_id = $i");
            command.Parameters.AddWithValue("$i", imageId);
            return Convert.ToString(command.ExecuteScalar()) is { Length: > 0 } result ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private bool HasAllRequiredPeople(long imageId, IReadOnlyList<PersonSummary> requiredPeople)
    {
        if (requiredPeople.Count == 0) return true;
        var confirmed = people.GetConfirmedPersonNamesForImage(imageId);
        return requiredPeople.All(person => confirmed.Contains(person.DisplayName, StringComparer.OrdinalIgnoreCase));
    }

    private sealed record Review(string? Markdown, IReadOnlyList<long>? PhotoIds, IReadOnlyList<long>? VideoIds);
}
