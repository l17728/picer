using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Video;
using Picer.Core.Vision;
using Picer.Core.Logging;
using System.Diagnostics;
using Picer.Core.Classification;

namespace Picer.Core.Search;

public sealed record AiQueryPlan(string? PersonName, string? Location, string? StartDate, string? EndDate,
    IReadOnlyList<string> Keywords, string Intent, IReadOnlyList<string>? LocationTerms = null);
public sealed record AiMediaAnswer(string Markdown, IReadOnlyList<ImageRecord> Photos,
    IReadOnlyList<VideoRecord> Videos, AiQueryPlan Plan)
{
    public string ConfirmedMarkdown { get; init; } = "";
    public string? AiExplanation { get; init; }
    public IReadOnlyDictionary<long, string> PhotoRecallSources { get; init; } = new Dictionary<long, string>();
    public string PhotoRecallSource(long id) => PhotoRecallSources.TryGetValue(id, out var source) ? source : "索引条件匹配";

    public static string ComposeMarkdown(string confirmedMarkdown, string? explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation)) return confirmedMarkdown;
        var quoted = string.Join('\n', explanation.Replace("\r\n", "\n").Split('\n').Select(line => "> " + line));
        return confirmedMarkdown + "\n\n## AI 解释（非检索证据）\n\n以下为模型生成解释，可能与证据不一致；数量、人物条件及可打开的媒体以程序确认结果为准。\n\n" + quoted;
    }
}

public sealed class AiMediaRetrievalService(PicerDatabase database, PeopleService people,
    VideoRepository videos, CloudVisionRepository cloud, OpenAiCompatibleChatClient chat,
    TimeSpan? modelPhaseTimeout = null, IAiSemanticRecall? semanticRecall = null,
    Func<CancellationToken, Task<ClassificationReplica?>>? classificationSource = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions EvidenceJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly TimeSpan _modelPhaseTimeout = modelPhaseTimeout ?? TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ModelProgressInterval = TimeSpan.FromSeconds(3);

    public async Task<AiMediaAnswer> AskAsync(string question, bool includeVideos, DateTime today,
        IProgress<(string Stage, int Percent)>? progress = null, CancellationToken cancellationToken = default,
        bool includeSuggestedClassifications = true, IProgress<AiMediaAnswer>? previews = null)
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
        var allPeople = people.GetNames();
        var explicitIds = questionMentions.Select(value => value.Person.Id).ToHashSet();
        var requiredPeople = allPeople.Where(value => explicitIds.Contains(value.Id)).ToArray();
        var normalizedLocation = NormalizeLocation(plan.Location);
        plan = plan with { PersonName = normalizedPerson, Location = normalizedLocation };
        var locationTerms = (plan.LocationTerms ?? [])
            .Concat(normalizedLocation is null ? [] : [normalizedLocation])
            .Concat(ExpandKnownLocationAliases(normalizedLocation))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        locationTerms = QueryRewrite.Expand(locationTerms).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
        var recallSources = new Dictionary<long, string>();
        // Time-of-day words are structured constraints, not text that must occur
        // in a description. Searching for "白天" literally used to discard all
        // otherwise matching photos because descriptions usually contain a clock
        // time (or no time phrase at all).
        baseFilter.TimeOfDay = dayPart switch { DayPart.Day => TimeOfDayFilter.Day, DayPart.Night => TimeOfDayFilter.Night, _ => TimeOfDayFilter.Any };
        ClassificationReplica? replica = null;
        string? classificationStatus = null;
        try
        {
            replica = classificationSource is null ? new ClassificationRepository(database).ExportReplica()
                : await classificationSource(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TimeoutException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { classificationStatus = "分类通道暂不可用，本次保留其他索引通道结果。"; }
        var classificationIndex = replica is null ? null : new ClassificationEvidenceIndex(replica, includeSuggestedClassifications);
        var conceptQuestion = questionMentions.Aggregate(question, (text, mentionValue) => text.Replace(
            mentionValue.MatchedName, new string(' ', mentionValue.MatchedName.Length), StringComparison.OrdinalIgnoreCase));
        var explicitTerms = (classificationIndex?.ExplicitTerms(conceptQuestion) ?? [])
            .Where(term => requiredPeople.Length < 2 || term != "合影").ToArray();
        if (explicitTerms.Length > 0)
        {
            // Preserve literal indexed concepts even when the planner omits or paraphrases them.
            // Extra model-invented visual conditions must not veto the user's explicit label.
            plan = plan with { Keywords = plan.Keywords.Where(term => question.Contains(term, StringComparison.OrdinalIgnoreCase)
                && term is not ("照片" or "图片" or "视频" or "查找" or "寻找"))
                .Concat(explicitTerms).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        }
        var visualTerms = QueryRewrite.Expand(plan.Keywords.Where(term => !IsDayPartToken(term))).ToArray();
        Task<AiSemanticRecallResult>? semanticTask = null;
        using var recallCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (visualTerms.Length > 0 && semanticRecall is not null && (normalizedLocation is null || bounds is not null))
        {
            var allowed = IndexSearchEngine.Search(database, baseFilter).Select(image => image.Id).ToHashSet();
            foreach (var person in requiredPeople.Where(person => person.Id != mention?.Id))
            {
                var filter = Clone(baseFilter); filter.PersonId = person.Id;
                allowed.IntersectWith(IndexSearchEngine.Search(database, filter).Select(image => image.Id));
            }
            semanticTask = semanticRecall.SearchAsync(string.Join(" ", plan.Keywords), allowed, recallCancellation.Token);
        }
        try
        {
        var searchTerms = visualTerms
            // Keep the literal location as a text fallback even when geocoding
            // returned bounds; older analyses may have place names but no GPS.
            .Concat(QueryRewrite.Expand(locationTerms))
            .ToArray();
        var toolResult = new MediaSearchTool(database, videos).ExecuteAll(new MediaSearchRequest(
            MediaTypeFilter.Photo, requiredPeople.Select(value => value.Id).ToArray(), bounds, start, end,
            baseFilter.TimeOfDay, Terms: bounds is null ? searchTerms : visualTerms, AutoRelax: false, IncludeMissing: false, Limit: 200,
            SortDirection: SortDirection.Descending,
            IncludeSuggestedClassifications: includeSuggestedClassifications));
        foreach (var hit in toolResult.Items)
        {
            var image = database.GetImage(hit.Id);
            if (image is not null && HasAllRequiredPeople(image.Id, requiredPeople)) photoCandidates[image.Id] = image;
        }
        AppLogger.Info($"AiQuestion photo retrieval completed; primaryHits={toolResult.Items.Count}; uniquePhotos={photoCandidates.Count}; geoFilter={(bounds is not null)}; elapsedMs={stopwatch.ElapsedMilliseconds}");
        var fallbackStarted = stopwatch.ElapsedMilliseconds;
        if (bounds is not null && locationTerms.Length > 0)
        {
            // Location aliases may recover records without GPS; a known GPS
            // conflict is still rejected below.
            var textFallback = new MediaSearchTool(database).ExecuteAll(new MediaSearchRequest(
                MediaTypeFilter.Photo, requiredPeople.Select(value => value.Id).ToArray(), null, start, end,
                baseFilter.TimeOfDay, Terms: locationTerms, IncludeMissing: false, Limit: 200,
                SortDirection: SortDirection.Descending,
                IncludeSuggestedClassifications: includeSuggestedClassifications));
            foreach (var hit in textFallback.Items)
            {
                var image = database.GetImage(hit.Id);
                if (image is not null && HasAllRequiredPeople(image.Id, requiredPeople) && !ConflictingLocation(image.Gps, bounds)) photoCandidates[image.Id] = image;
            }
        }

        AppLogger.Info($"AiQuestion text fallback completed; elapsedMs={stopwatch.ElapsedMilliseconds - fallbackStarted}; uniquePhotos={photoCandidates.Count}");
        IReadOnlyDictionary<long, IReadOnlyList<ClassificationEvidence>> classificationEvidence = new Dictionary<long, IReadOnlyList<ClassificationEvidence>>();
        if (visualTerms.Length > 0)
        {
            if (classificationIndex is not null)
            {
                classificationEvidence = classificationIndex.Match(database, plan.Keywords.Where(term => !IsDayPartToken(term)).ToArray(), combineTextEvidence: true);
                var allowed = IndexSearchEngine.Search(database, baseFilter).Select(image => image.Id).ToHashSet();
                if (normalizedLocation is not null && bounds is null) allowed.Clear();
                if (locationTerms.Length > 0)
                {
                    var locationMatches = new MediaSearchTool(database).ExecuteAll(new MediaSearchRequest(MediaTypeFilter.Photo,
                        requiredPeople.Select(person => person.Id).ToArray(), StartDate: start, EndDate: end,
                        TimeOfDay: baseFilter.TimeOfDay, Terms: locationTerms, IncludeMissing: false, Limit: 200));
                    allowed.UnionWith(locationMatches.Items.Select(hit => hit.Id));
                }
                foreach (var id in classificationEvidence.Keys.Where(allowed.Contains))
                {
                    var image = database.GetImage(id);
                    if (image is null || !HasAllRequiredPeople(id, requiredPeople) || ConflictingLocation(image.Gps, bounds)) continue;
                    photoCandidates[id] = image;
                    recallSources[id] = "分类标签 · " + string.Join("、", classificationEvidence[id].Select(label => label.Name + "（" + label.Source + "）"));
                }
            }
        }
        var requiredVisualTerms = plan.Keywords.Where(term => !IsDayPartToken(term)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requiredVisualTerms.Length > 0)
        {
            // Each requested visual condition may be supported by a different indexed field.
            // Synonyms within one condition are alternatives, not additional mandatory conditions.
            HashSet<long>? matching = null;
            foreach (var term in requiredVisualTerms)
            {
                var ids = new MediaSearchTool(database).ExecuteAll(new(Terms: QueryRewrite.Expand([term]), AutoRelax: false))
                    .Items.Select(hit => hit.Id).ToHashSet();
                if (classificationIndex is not null) ids.UnionWith(classificationIndex.Match(database, [term]).Keys);
                if (matching is null) matching = ids; else matching.IntersectWith(ids);
            }
            foreach (var id in photoCandidates.Keys.Where(id => matching is not null && !matching.Contains(id)).ToArray()) photoCandidates.Remove(id);
        }
        progress?.Report(("检索视频索引", 45));
        var videoStarted = stopwatch.ElapsedMilliseconds;
        var videoCandidates = includeVideos ? RetrieveVideos(requiredPeople.Select(person => person.Id).ToArray(), bounds, visualTerms, searchTerms, locationTerms, start, end, baseFilter.TimeOfDay).ToArray() : [];
        foreach (var term in requiredVisualTerms)
        {
            var matches = new MediaSearchTool(database, videos).ExecuteAll(new(MediaTypeFilter.Video, Terms: QueryRewrite.Expand([term]), AutoRelax: false))
                .Items.Select(hit => hit.Id).ToHashSet();
            videoCandidates = videoCandidates.Where(video => matches.Contains(video.Id)).ToArray();
        }
        if (normalizedLocation is not null)
        {
            var locationMatches = new MediaSearchTool(database, videos).ExecuteAll(new(MediaTypeFilter.All,
                Terms: locationTerms, AutoRelax: false));
            var locatedPhotos = locationMatches.Items.Where(hit => hit.MediaType == MediaTypeFilter.Photo).Select(hit => hit.Id).ToHashSet();
            var locatedVideos = locationMatches.Items.Where(hit => hit.MediaType == MediaTypeFilter.Video).Select(hit => hit.Id).ToHashSet();
            foreach (var photo in photoCandidates.Values.Where(photo => !locatedPhotos.Contains(photo.Id)
                && (bounds is null || !GeoPlaceResolver.IsWithin(photo.Gps, bounds.MinLatitude, bounds.MaxLatitude, bounds.MinLongitude, bounds.MaxLongitude))).ToArray())
                photoCandidates.Remove(photo.Id);
            videoCandidates = videoCandidates.Where(video => locatedVideos.Contains(video.Id)
                || bounds is not null && VideoSearchMetadata.Within(video, bounds)).ToArray();
        }
        var indexPhotoIds = photoCandidates.Keys.ToHashSet();
        foreach (var id in indexPhotoIds)
            if (!recallSources.ContainsKey(id)) recallSources[id] = requiredVisualTerms.Length > 0 ? "索引文字候选 · 需核对画面" : "人物、时间或地点索引条件";
        var photos = photoCandidates.Values.ToArray();
        string? recallStatus = classificationStatus;
        if (semanticTask is not null)
        {
            previews?.Report(CreateAnswer(photos, videoCandidates, requiredPeople, plan, null, includeSuggestedClassifications));
            progress?.Report(("索引候选已就绪，正在补充中文语义结果", 55));
            var semantic = await semanticTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            recallStatus = string.Join(" ", new[] { classificationStatus, semantic.Status }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var allowedNow = IndexSearchEngine.Search(database, baseFilter).Select(image => image.Id).ToHashSet();
            var semanticImages = semantic.ImageIds.Where(allowedNow.Contains).Distinct().Select(database.GetImage)
                .Where(image => image is not null && HasAllRequiredPeople(image.Id, requiredPeople)).Cast<ImageRecord>().ToArray();
            var fused = ReciprocalRankFusion.Fuse([
                ("index", photoCandidates.Keys.Select((id, rank) => new RankedCandidate(id, rank + 1))),
                ("semantic", semanticImages.Select((image, rank) => new RankedCandidate(image.Id, rank + 1)))
            ], Math.Max(1, photoCandidates.Count + semanticImages.Length), cancellationToken: cancellationToken);
            foreach (var image in semanticImages) photoCandidates[image.Id] = image;
            photos = fused.Select(hit => photoCandidates[hit.MediaId]).ToArray();
            foreach (var hit in fused)
                recallSources[hit.MediaId] = recallSources.TryGetValue(hit.MediaId, out var classificationRecall) ? classificationRecall : hit.SourceRanks.ContainsKey("semantic")
                    ? hit.SourceRanks.ContainsKey("index") ? "索引匹配 · 视觉相似" : "视觉相似 · 已校验明确条件"
                    : "索引条件匹配";
        }
        AppLogger.Info($"AiQuestion video retrieval completed; elapsedMs={stopwatch.ElapsedMilliseconds - videoStarted}; videos={videoCandidates.Length}");
        if (photos.Length == 0 && videoCandidates.Length == 0)
        {
            progress?.Report(("完成", 100));
            AppLogger.Info($"AiQuestion completed without evidence; photos=0; videos=0; elapsedMs={stopwatch.ElapsedMilliseconds}");
            return RecallNote(CreateAnswer([], [], requiredPeople, plan, null, includeSuggestedClassifications), recallStatus);
        }
        // The index enforces explicit identities, dates and places. A second
        // model adds no evidence to a purely structured query.
        if (visualTerms.Length == 0 && (requiredPeople.Length > 0 || bounds is not null || start is not null || end is not null))
        {
            progress?.Report(("完成", 100));
            AppLogger.Info($"AiQuestion structured result ready; photos={photos.Length}; videos={videoCandidates.Length}; elapsedMs={stopwatch.ElapsedMilliseconds}");
            return CreateAnswer(photos, videoCandidates, requiredPeople, plan, null, includeSuggestedClassifications);
        }
        progress?.Report(("AI复核候选", 65));
        var evidenceStarted = stopwatch.ElapsedMilliseconds;
        object Evidence(int photoCount, int videoCount) => new
        {
            question,
            plan,
            totalIndexPhotos = indexPhotoIds.Count,
            totalIndexVideos = videoCandidates.Length,
            sampleOnly = photos.Length > photoCount || videoCandidates.Length > videoCount,
            photos = photos.Take(photoCount).Select(image => new { id = image.Id, file = Path.GetFileName(image.Path),
                time = image.TakenUtc ?? image.CreatedUtc ?? image.ModifiedUtc,
                gps = image.Gps,
                location = GetLocationDisplay(image.Id),
                confirmedPersonNames = people.GetConfirmedPersonNamesForImage(image.Id),
                requiredPersonNames = requiredPeople.Select(value => value.DisplayName).ToArray(),
                classifications = classificationEvidence.GetValueOrDefault(image.Id, []).Take(12),
                description = Truncate(cloud.GetLatestCompleted(image.Id)?.Result.Summary, 1000),
                indexedEvidence = PhotoEvidence(image.Id),
                recall = recallSources.GetValueOrDefault(image.Id, "索引文字匹配") }).ToArray(),
            videos = videoCandidates.Take(videoCount).Select(video => new { id = video.Id, file = Path.GetFileName(video.Path),
                time = video.TakenUtc ?? video.ModifiedUtc, durationSeconds = video.DurationSeconds, gps = video.Gps,
                requiredPersonNames = requiredPeople.Select(value => value.DisplayName).ToArray(),
                confirmedPersonNames = people.GetConfirmedPersonNamesForVideo(video.Id),
                transcript = Truncate(videos.GetTranscript(video.Id), 1500), summary = Truncate(videos.GetSummary(video.Id), 1500) }).ToArray()
        };
        var samplePhotos = Math.Min(30, photos.Length);
        var sampleVideos = Math.Min(20, videoCandidates.Length);
        var evidenceJson = JsonSerializer.Serialize(Evidence(samplePhotos, sampleVideos), EvidenceJson);
        while (evidenceJson.Length > 24000 && (samplePhotos > 1 || sampleVideos > 1))
        {
            samplePhotos = Math.Min(samplePhotos, Math.Max(1, samplePhotos / 2));
            sampleVideos = Math.Min(sampleVideos, Math.Max(1, sampleVideos / 2));
            evidenceJson = JsonSerializer.Serialize(Evidence(samplePhotos, sampleVideos), EvidenceJson);
        }
        AppLogger.Info($"AiQuestion evidence prepared; elapsedMs={stopwatch.ElapsedMilliseconds - evidenceStarted}; photos={photos.Length}; videos={videoCandidates.Length}");
        string reviewText;
        try
        {
        reviewText = await CompleteWithProgressAsync("正在核对候选结果", "结果复核", """
            你是私人媒体库检索复核器。只能从给定候选ID中选择，不得编造ID或人物身份。
            confirmedPersonNames 是程序依据人工确认人脸生成的权威字段；问题指定人物时，必须保留该字段包含目标人物的候选，不能把目标人物改写成其他姓名。
            输出JSON：markdown、photoIds、videoIds。markdown使用中文Markdown，说明结论、命中依据和不确定项，照片与视频分节。
            没有可靠结果时明确说明。photoIds和videoIds必须是数字数组。
            分类、描述、标签、相册和地点文字都是索引证据，模型建议不是人工确认事实。此处可能只是有限样本，不能声称样本数量等于全部命中。
            对索引命中仅建议排序，不因缺少照片描述而否定已有分类或手工标签；视觉相似候选必须说明不确定性。
            """, evidenceJson, 900, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException or InvalidOperationException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            progress?.Report(("索引结果已保留，模型复核暂不可用", 100));
            var retained = photos.Where(photo => indexPhotoIds.Contains(photo.Id)).ToArray();
            return RecallNote(CreateAnswer(retained, videoCandidates, requiredPeople, plan, null, includeSuggestedClassifications),
                string.Join(" ", new[] { recallStatus, "模型复核暂不可用，已保留全部索引证据命中。" }.Where(value => !string.IsNullOrWhiteSpace(value))))
                with { PhotoRecallSources = retained.ToDictionary(photo => photo.Id, photo => recallSources.GetValueOrDefault(photo.Id, "索引条件匹配")) };
        }
        Review? review;
        try { review = JsonSerializer.Deserialize<Review>(OpenAiCompatibleChatClient.StripFence(reviewText), Json); }
        catch (JsonException) { review = null; }
        if (review is null || review.PhotoIds is null || review.VideoIds is null || string.IsNullOrWhiteSpace(review.Markdown))
            review = new Review($"找到 {photos.Length} 张候选照片和 {videoCandidates.Length} 个候选视频。AI 复核未返回完整结果，请核对候选内容。",
                photos.Select(value => value.Id).ToArray(), videoCandidates.Select(value => value.Id).ToArray());
        var allowedPhotos = photos.ToDictionary(value => value.Id);
        var allowedVideos = videoCandidates.ToDictionary(value => value.Id);
        var rankedPhotos = (review.PhotoIds ?? []).Where(allowedPhotos.ContainsKey).Concat(photos.Where(photo => indexPhotoIds.Contains(photo.Id)).Select(photo => photo.Id))
            .Distinct().Select(id => allowedPhotos[id]).ToArray();
        var rankedVideos = (review.VideoIds ?? []).Where(allowedVideos.ContainsKey).Concat(videoCandidates.Select(video => video.Id))
            .Distinct().Select(id => allowedVideos[id]).ToArray();
        if (indexPhotoIds.Except(review.PhotoIds ?? []).Any() || videoCandidates.Any(video => !(review.VideoIds ?? []).Contains(video.Id)))
            review = review with { Markdown = "已保留符合索引条件的全部命中；模型复核仅调整排序，未据此删除索引证据。" };
        if (mention is not null && rankedPhotos.Length == 0 && rankedVideos.Length == 0)
        {
            rankedPhotos = photos.Where(image => HasAllRequiredPeople(image.Id, requiredPeople)).ToArray();
            rankedVideos = videoCandidates.Where(video => people.GetConfirmedPersonNamesForVideo(video.Id)
                .Contains(mention.DisplayName, StringComparer.OrdinalIgnoreCase)).ToArray();
            AppLogger.Info($"AiQuestion reviewer fallback applied for confirmed person; photos={rankedPhotos.Length}; videos={rankedVideos.Length}");
        }
        progress?.Report(("完成", 100));
        return RecallNote(CreateAnswer(rankedPhotos, rankedVideos, requiredPeople, plan, review.Markdown, includeSuggestedClassifications), recallStatus)
            with { PhotoRecallSources = rankedPhotos.ToDictionary(photo => photo.Id, photo => recallSources.GetValueOrDefault(photo.Id, "索引条件匹配")) };
        }
        finally
        {
            recallCancellation.Cancel();
            if (semanticTask is not null)
            {
                try { await semanticTask.ConfigureAwait(false); }
                catch (Exception) { /* Observe optional recall even if primary retrieval failed. */ }
            }
        }
    }

    private static AiMediaAnswer RecallNote(AiMediaAnswer answer, string? note)
    {
        if (note is null) return answer;
        var confirmed = answer.ConfirmedMarkdown + "\n\n" + note;
        return answer with { ConfirmedMarkdown = confirmed, Markdown = AiMediaAnswer.ComposeMarkdown(confirmed, answer.AiExplanation) };
    }

    private static AiMediaAnswer CreateAnswer(IReadOnlyList<ImageRecord> photos, IReadOnlyList<VideoRecord> videos,
        IReadOnlyList<PersonSummary> requiredPeople, AiQueryPlan plan, string? explanation,
        bool includeSuggestedClassifications)
    {
        // Free-form prose is not verifiable evidence of a person's identity.
        // Always use the authoritative intersection for person queries.
        if (requiredPeople.Count > 0) explanation = null;
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
        IReadOnlyList<string> visualTerms, IReadOnlyList<string> searchTerms, IReadOnlyList<string> locationTerms,
        DateTime? start, DateTime? end, TimeOfDayFilter timeOfDay)
    {
        var tool = new MediaSearchTool(database, videos);
        var result = tool.ExecuteAll(new(MediaTypeFilter.Video, personIds, bounds,
            StartDate: start, EndDate: end, TimeOfDay: timeOfDay,
            Terms: bounds is null ? searchTerms : visualTerms, AutoRelax: false, Limit: 50));
        var hits = result.Items.AsEnumerable();
        if (bounds is not null && locationTerms.Count > 0)
        {
            var fallback = tool.ExecuteAll(new(MediaTypeFilter.Video, personIds,
                StartDate: start, EndDate: end, TimeOfDay: timeOfDay,
                Terms: locationTerms, AutoRelax: false, Limit: 50));
            hits = hits.Concat(fallback.Items);
        }
        var records = videos.GetAll().ToDictionary(video => video.Id);
        return hits.DistinctBy(hit => hit.Id).Where(hit => records.ContainsKey(hit.Id))
            .Select(hit => records[hit.Id]).Where(video => !ConflictingLocation(video.Gps, bounds)).ToArray();
    }

    private static bool ConflictingLocation(string? gps, GeoBounds? bounds) => bounds is not null
        && GeoPlaceResolver.TryParseCoordinates(gps, out _, out _)
        && !GeoPlaceResolver.IsWithin(gps, bounds.MinLatitude, bounds.MaxLatitude, bounds.MinLongitude, bounds.MaxLongitude);

    private static SearchFilters Clone(SearchFilters value) => new() { PersonId = value.PersonId, GeoBounds = value.GeoBounds,
        TakenAfter = value.TakenAfter, TakenBefore = value.TakenBefore, IncludeMissing = value.IncludeMissing,
        TimeOfDay = value.TimeOfDay,
        SortBy = value.SortBy, SortDir = value.SortDir };
    private IReadOnlyList<string> PhotoEvidence(long id)
    {
        using var command = database.Command("""
            SELECT '手工标签' kind,t.name value FROM image_tags it JOIN tags t ON t.id=it.tag_id WHERE it.image_id=$id
            UNION ALL SELECT '相册',a.name||' '||COALESCE(a.description,'') FROM album_images ai JOIN albums a ON a.id=ai.album_id WHERE ai.image_id=$id
            UNION ALL SELECT '地点文字',search_text FROM image_locations WHERE image_id=$id
            UNION ALL SELECT '本地视觉描述',result_text FROM florence_image_results WHERE image_id=$id AND state='accepted'
            UNION ALL SELECT '目录描述',sg.summary FROM scene_group_descriptions sg, json_each(sg.image_ids_json) member
                WHERE member.value=$id AND sg.state='accepted' AND sg.provider<>'picer-local-layout'
            UNION ALL SELECT '图片描述',summary FROM cloud_vision_analyses WHERE image_id=$id AND status='completed'
            UNION ALL SELECT '描述检索词',j.value FROM cloud_vision_analyses c,
                json_each(CASE WHEN json_valid(c.result_json) THEN c.result_json ELSE '{}' END,'$.SearchTerms') j
                WHERE c.image_id=$id AND c.status='completed' AND j.type='text'
            UNION ALL SELECT '描述活动',j.value FROM cloud_vision_analyses c,
                json_each(CASE WHEN json_valid(c.result_json) THEN c.result_json ELSE '{}' END,'$.Activities') j
                WHERE c.image_id=$id AND c.status='completed' AND j.type='text'
            LIMIT 8
            """);
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) if (!reader.IsDBNull(1)) result.Add(reader.GetString(0) + ":" + Truncate(reader.GetString(1), 200));
        return result;
    }

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
