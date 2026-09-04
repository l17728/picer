using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Core.Search;

public sealed record AiQueryPlan(string? PersonName, string? Location, string? StartDate, string? EndDate,
    IReadOnlyList<string> Keywords, string Intent);
public sealed record AiMediaAnswer(string Markdown, IReadOnlyList<ImageRecord> Photos,
    IReadOnlyList<VideoRecord> Videos, AiQueryPlan Plan);

public sealed class AiMediaRetrievalService(PicerDatabase database, PeopleService people,
    VideoRepository videos, CloudVisionRepository cloud, OpenAiCompatibleChatClient chat)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<AiMediaAnswer> AskAsync(string question, bool includeVideos, DateTime today,
        IProgress<(string Stage, int Percent)>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(("理解问题", 5));
        var names = people.GetAll().Select(value => value.DisplayName).ToArray();
        var planText = await chat.CompleteAsync("""
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

        progress?.Report(("检索照片索引", 25));
        var mention = !string.IsNullOrWhiteSpace(plan.PersonName)
            ? people.FindByName(plan.PersonName.Trim()) ??
              (people.GetAll().FirstOrDefault(person =>
                  string.Equals(person.DisplayName.Trim(), plan.PersonName.Trim(), StringComparison.OrdinalIgnoreCase)) is { } summary
                  ? people.Get(summary.Id)
                  : null)
            : people.FindMention(question)?.Person;
        var bounds = string.IsNullOrWhiteSpace(plan.Location) ? null : GeoPlaceResolver.Resolve(plan.Location);
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
        var searchTerms = plan.Keywords
            .Where(term => !IsDayPartToken(term))
            .Concat(bounds is null && !string.IsNullOrWhiteSpace(plan.Location) ? [plan.Location!] : [])
            .ToArray();
        if (searchTerms.Length == 0)
            foreach (var image in IndexSearchEngine.Search(database, baseFilter).Take(100)) photoCandidates[image.Id] = image;
        else
            foreach (var term in searchTerms)
            {
                var filter = Clone(baseFilter); filter.Query = term;
                foreach (var image in IndexSearchEngine.Search(database, filter).Take(100)) photoCandidates[image.Id] = image;
            }

        progress?.Report(("检索视频索引", 45));
        var videoCandidates = includeVideos ? RetrieveVideos(plan, mention?.Id, start, end).Take(50).ToArray() : [];
        var photos = photoCandidates.Values.Take(50).ToArray();
        progress?.Report(("AI复核候选", 65));
        var evidence = new
        {
            question,
            plan,
            photos = photos.Select(image => new { id = image.Id, file = Path.GetFileName(image.Path),
                time = image.TakenUtc ?? image.CreatedUtc ?? image.ModifiedUtc,
                description = cloud.GetLatestCompleted(image.Id)?.Result.Summary }).ToArray(),
            videos = videoCandidates.Select(video => new { id = video.Id, file = Path.GetFileName(video.Path),
                time = video.TakenUtc ?? video.ModifiedUtc, durationSeconds = video.DurationSeconds,
                transcript = Truncate(videos.GetTranscript(video.Id), 500), summary = videos.GetSummary(video.Id) }).ToArray()
        };
        var reviewText = await chat.CompleteAsync("""
            你是私人媒体库检索复核器。只能从给定候选ID中选择，不得编造ID或人物身份。
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
        progress?.Report(("完成", 100));
        return new(review.Markdown ?? "未生成回答。", rankedPhotos, rankedVideos, plan);
    }

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

    private sealed record Review(string? Markdown, IReadOnlyList<long>? PhotoIds, IReadOnlyList<long>? VideoIds);
}
