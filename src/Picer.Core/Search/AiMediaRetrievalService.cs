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
    IReadOnlyList<VideoRecord> Videos, AiQueryPlan Plan)
{
    public string ConfirmedMarkdown { get; init; } = "";
    public string? AiExplanation { get; init; }

    public static string ComposeMarkdown(string confirmedMarkdown, string? explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation)) return confirmedMarkdown;
        var quoted = string.Join('\n', explanation.Replace("\r\n", "\n").Split('\n').Select(line => "> " + line));
        return confirmedMarkdown + "\n\n## AI 解释（非检索证据）\n\n以下为模型生成解释，可能与证据不一致；数量、人物条件及可打开的媒体以程序确认结果为准。\n\n" + quoted;
    }
}

public sealed class AiMediaRetrievalService(PicerDatabase database, PeopleService people,
    VideoRepository videos, CloudVisionRepository cloud, OpenAiCompatibleChatClient chat,
    TimeSpan? modelPhaseTimeout = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly TimeSpan _modelPhaseTimeout = modelPhaseTimeout ?? TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ModelProgressInterval = TimeSpan.FromSeconds(3);

    public async Task<AiMediaAnswer> AskAsync(string question, bool includeVideos, DateTime today,
        IProgress<(string Stage, int Percent)>? progress = null, CancellationToken cancellationToken = default,
        bool includeSuggestedClassifications = false)
    {
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(("准备查询", 2));
        var planText = await CompleteWithProgressAsync("正在理解问题", "查询规划", """
            Output JSON field locationTerms as an array of alternative place names and administrative expressions for recall (for example Shanghai, Shanghai City). Do not use these terms as final proof; they are only candidate retrieval hints.
            你是私人媒体库的查询规划器。只输出JSON：personName、location、startDate、endDate、keywords、intent、locationTerms。只能抽取用户问题中明确出现的人物名，不得猜测或扩写人物名。人物身份会由本地人工确认索引核对。
            日期必须为yyyy-MM-dd；keywords是字符串数组，只保留人物/地点/日期之外的服装、颜色、活动、物体和场景词。
            不得虚构人物名称。无法确定的字段为null。
            """, $"今天：{today:yyyy-MM-dd}\n是否包含视频：{includeVideos}\n问题：{question}",
            450, progress, cancellationToken).ConfigureAwait(false);
        AiQueryPlan plan;
        try { plan = JsonSerializer.Deserialize<AiQueryPlan>(OpenAiCompatibleChatClient.StripFence(planText), Json)!; }
        catch (Exception exception) { throw new InvalidDataException("AI 未能生成有效的结构化查询。", exception); }
        if (plan is null) throw new InvalidDataException("AI 查询计划为空。");
        plan = plan with { Keywords = plan.Keywords?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct().ToArray() ?? [] };
        AppLogger.Info($"AiQuestion plan ready; hasPerson={(!string.IsNullOrWhiteSpace(plan.PersonName))}; hasLocation={(!string.IsNullOrWhiteSpace(plan.Location))}; hasDate={(!string.IsNullOrWhiteSpace(plan.StartDate) || !string.IsNullOrWhiteSpace(plan.EndDate))}; keywordCount={plan.Keywords.Count}; elapsedMs={stopwatch.ElapsedMilliseconds}");

        progress?.Report(("检索照片索引", 25));
        // An explicit confirmed name in the user's question is authoritative.
        // Do not let a planner typo or hallucination replace it.
        var questionMentions = people.FindMentions(question);
        var questionMention = questionMentions.FirstOrDefault();
        var normalizedPerson = questionMention?.Person.DisplayName;
        var allPeople = people.GetAll();
        var explicitIds = questionMentions.Select(value => value.Person.Id).ToHashSet();
        var requiredPeople = allPeople.Where(value => explicitIds.Contains(value.Id)).ToArray();
        var normalizedLocation = NormalizeLocation(plan.Location);
        plan = plan with { PersonName = normalizedPerson, Location = normalizedLocation };
        var locationTerms = (plan.LocationTerms ?? [])
            .Concat(normalizedLocation is null ? [] : [normalizedLocation])
            .Concat(ExpandKnownLocationAliases(normalizedLocation))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var mention = questionMention?.Person;
        if (questionMention is not null)
            AppLogger.Info($"AiQuestion person constraint pinned to explicit mention; aliasLength={questionMention.MatchedName.Length}");
        var bounds = string.IsNullOrWhiteSpace(normalizedLocation) ? null : GeoPlaceResolver.Resolve(normalizedLocation);
        AppLogger.Info($"AiQuestion constraints normalized; personBound={mention is not null}; personConstraintCount={requiredPeople.Length}; locationResolved={bounds is not null}; locationTermCount={locationTerms.Length}; selfReference={ContainsSelfReference(question)}");
        DateTime? start = DateTime.TryParse(plan.StartDate, out var startValue) ? startValue.Date.ToUniversalTime() : null;
        DateTime? end = DateTime.TryParse(plan.EndDate, out var endValue) ? endValue.Date.AddDays(1).AddTicks(-1).ToUniversalTime() : null;
        var dayPart = ResolveDayPart(question, plan.Keywords);
        var baseFilter = new SearchFilters { PersonId = mention?.Id, GeoBounds = bounds, TakenAfter = start,
            TakenBefore = end, IncludeMissing = false, SortBy = SortField.TakenDate, SortDir = SortDirection.Descending,
            IncludeSuggestedClassifications = includeSuggestedClassifications };
        var photoCandidates = new Dictionary<long, ImageRecord>();
        // Time-of-day words are structured constraints, not text that must occur
        // in a description. Searching for "白天" literally used to discard all
        // otherwise matching photos because descriptions usually contain a clock
        // time (or no time phrase at all).
        baseFilter.TimeOfDay = dayPart switch { DayPart.Day => TimeOfDayFilter.Day, DayPart.Night => TimeOfDayFilter.Night, _ => TimeOfDayFilter.Any };
        var visualTerms = QueryRewrite.Expand(plan.Keywords.Where(term => !IsDayPartToken(term))).ToArray();
        var visualConcepts = VisualConceptCatalog.Parse(string.Join(" ", visualTerms)).ConceptIds;
        var searchTerms = visualTerms
            // Keep the literal location as a text fallback even when geocoding
            // returned bounds; older analyses may have place names but no GPS.
            .Concat(QueryRewrite.Expand(locationTerms))
            .ToArray();
        var toolResult = new MediaSearchTool(database, videos).Execute(new MediaSearchRequest(
            MediaTypeFilter.Photo, requiredPeople.Select(value => value.Id).ToArray(), bounds, start, end,
            baseFilter.TimeOfDay, Terms: bounds is null ? searchTerms : visualTerms, AutoRelax: false, IncludeMissing: false, Limit: 200,
            SortDirection: SortDirection.Descending, VisualConceptIds: visualConcepts,
            IncludeSuggestedClassifications: includeSuggestedClassifications));
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
                MediaTypeFilter.Photo, requiredPeople.Select(value => value.Id).ToArray(), null, start, end,
                baseFilter.TimeOfDay, Terms: searchTerms, IncludeMissing: false, Limit: 200,
                SortDirection: SortDirection.Descending, VisualConceptIds: visualConcepts,
                IncludeSuggestedClassifications: includeSuggestedClassifications));
            foreach (var hit in textFallback.Items)
            {
                var image = database.GetImage(hit.Id);
                if (image is not null && HasAllRequiredPeople(image.Id, requiredPeople)) photoCandidates[image.Id] = image;
            }
        }

        progress?.Report(("检索视频索引", 45));
        var videoCandidates = includeVideos ? RetrieveVideos(requiredPeople.Select(person => person.Id).ToArray(), bounds, visualTerms, searchTerms, locationTerms.Length > 0, start, end, baseFilter.TimeOfDay).Take(50).ToArray() : [];
        var photos = photoCandidates.Values.Take(50).ToArray();
        if (photos.Length == 0 && videoCandidates.Length == 0)
        {
            progress?.Report(("完成", 100));
            AppLogger.Info($"AiQuestion completed without evidence; photos=0; videos=0; elapsedMs={stopwatch.ElapsedMilliseconds}");
            return CreateAnswer([], [], requiredPeople, plan, null, includeSuggestedClassifications);
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
                time = video.TakenUtc ?? video.ModifiedUtc, durationSeconds = video.DurationSeconds, gps = video.Gps,
                requiredPersonNames = requiredPeople.Select(value => value.DisplayName).ToArray(),
                confirmedPersonNames = people.GetConfirmedPersonNamesForVideo(video.Id),
                transcript = Truncate(videos.GetTranscript(video.Id), 500), summary = videos.GetSummary(video.Id) }).ToArray()
        };
        var reviewText = await CompleteWithProgressAsync("正在核对候选结果", "结果复核", """
            你是私人媒体库检索复核器。只能从给定候选ID中选择，不得编造ID或人物身份。
            confirmedPersonNames 是程序依据人工确认人脸生成的权威字段；问题指定人物时，必须保留该字段包含目标人物的候选，不能把目标人物改写成其他姓名。
            输出JSON：markdown、photoIds、videoIds。markdown使用中文Markdown，说明结论、命中依据和不确定项，照片与视频分节。
            没有可靠结果时明确说明。photoIds和videoIds必须是数字数组。
            """, JsonSerializer.Serialize(evidence), 900, progress, cancellationToken).ConfigureAwait(false);
        Review? review;
        try { review = JsonSerializer.Deserialize<Review>(OpenAiCompatibleChatClient.StripFence(reviewText), Json); }
        catch (JsonException) { review = null; }
        if (review is null || review.PhotoIds is null || review.VideoIds is null || string.IsNullOrWhiteSpace(review.Markdown))
            review = new Review($"找到 {photos.Length} 张候选照片和 {videoCandidates.Length} 个候选视频。AI 复核未返回完整结果，请核对候选内容。",
                photos.Select(value => value.Id).ToArray(), videoCandidates.Select(value => value.Id).ToArray());
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
        progress?.Report(("完成", 100));
        return CreateAnswer(rankedPhotos, rankedVideos, requiredPeople, plan, review.Markdown, includeSuggestedClassifications);
    }

    private static AiMediaAnswer CreateAnswer(IReadOnlyList<ImageRecord> photos, IReadOnlyList<VideoRecord> videos,
        IReadOnlyList<PersonSummary> requiredPeople, AiQueryPlan plan, string? explanation,
        bool includeSuggestedClassifications)
    {
        var text = new System.Text.StringBuilder("## 程序确认结果\n\n");
        if (includeSuggestedClassifications)
            text.Append("已允许待确认的 AI 分类建议参与照片检索；相关命中不代表该分类已由你确认。\n\n");
        text.Append($"返回照片 **{photos.Count} 张**、视频 **{videos.Count} 个**；下方媒体列表与这些数量一致。\n\n");
        if (requiredPeople.Count > 0)
            text.Append("人物条件：所有返回项均包含人工确认人物“")
                .Append(string.Join("、", requiredPeople.Select(person => Inline(person.DisplayName)))).Append("”（交集）。\n\n");
        if (!string.IsNullOrWhiteSpace(plan.StartDate) || !string.IsNullOrWhiteSpace(plan.EndDate))
            text.Append("时间检索条件：").Append(Inline(plan.StartDate ?? "不限")).Append(" 至 ").Append(Inline(plan.EndDate ?? "不限")).Append("。\n\n");
        if (!string.IsNullOrWhiteSpace(plan.Location))
            text.Append("地点检索条件：").Append(Inline(plan.Location)).Append("；通过 GPS 或索引文字召回，地点文字命中仍需核对。\n\n");
        if (plan.Keywords.Count > 0)
            text.Append("文字检索条件：").Append(string.Join("、", plan.Keywords.Select(Inline))).Append("；候选描述匹配不等同于人工核实。\n\n");
        if (photos.Count == 0 && videos.Count == 0)
        {
            text.Append("未找到可返回的匹配内容。当前没有可由媒体列表核实的命中，模型的命中声明未予采用。");
            explanation = null;
        }
        var confirmed = text.ToString().TrimEnd();
        return new(AiMediaAnswer.ComposeMarkdown(confirmed, explanation), photos, videos, plan)
            { ConfirmedMarkdown = confirmed, AiExplanation = explanation };

        static string Inline(string value) => value.Replace('\r', ' ').Replace('\n', ' ')
            .Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_").Replace("[", "\\[").Replace("]", "\\]").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private async Task<string> CompleteWithProgressAsync(string stage, string phase, string systemPrompt,
        string userPrompt, int maximumTokens, IProgress<(string Stage, int Percent)>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_modelPhaseTimeout);
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(($"{stage}（请求已发送，等待模型返回）", 10));
        AppLogger.Info($"AiQuestion model phase started; phase={phase}; timeoutSeconds={_modelPhaseTimeout.TotalSeconds:0}");
        var request = chat.CompleteAsync(systemPrompt, userPrompt, maximumTokens, timeout.Token);
        while (!request.IsCompleted)
        {
            var delay = Task.Delay(ModelProgressInterval, cancellationToken);
            if (await Task.WhenAny(request, delay).ConfigureAwait(false) == request) break;
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(($"{stage}（模型处理中，已等待 {stopwatch.Elapsed.TotalSeconds:0} 秒）", 10));
        }
        try
        {
            var result = await request.ConfigureAwait(false);
            AppLogger.Info($"AiQuestion model phase completed; phase={phase}; elapsedMs={stopwatch.ElapsedMilliseconds}");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLogger.Warn($"AiQuestion model phase timed out; phase={phase}; elapsedMs={stopwatch.ElapsedMilliseconds}");
            throw new TimeoutException($"AI 问答的{stage}超过 {_modelPhaseTimeout.TotalSeconds:0} 秒未完成。请检查 IndexService/模型服务状态后重试。");
        }
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

    private IReadOnlyList<VideoRecord> RetrieveVideos(IReadOnlyList<long> personIds, GeoBounds? bounds,
        IReadOnlyList<string> visualTerms, IReadOnlyList<string> searchTerms, bool hasLocationTerms,
        DateTime? start, DateTime? end, TimeOfDayFilter timeOfDay)
    {
        var tool = new MediaSearchTool(database, videos);
        var result = tool.Execute(new(MediaTypeFilter.Video, personIds, bounds,
            StartDate: start, EndDate: end, TimeOfDay: timeOfDay,
            Terms: bounds is null ? searchTerms : visualTerms, AutoRelax: false, Limit: 50));
        var hits = result.Items.AsEnumerable();
        if (bounds is not null && hasLocationTerms)
        {
            var fallback = tool.Execute(new(MediaTypeFilter.Video, personIds,
                StartDate: start, EndDate: end, TimeOfDay: timeOfDay,
                Terms: searchTerms, AutoRelax: false, Limit: 50));
            hits = hits.Concat(fallback.Items);
        }
        var records = videos.GetAll().ToDictionary(video => video.Id);
        return hits.DistinctBy(hit => hit.Id).Where(hit => records.ContainsKey(hit.Id)).Take(50).Select(hit => records[hit.Id]).ToArray();
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
