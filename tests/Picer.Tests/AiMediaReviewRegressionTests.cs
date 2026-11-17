using System.Net;
using System.Text;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Search;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class AiMediaReviewRegressionTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitCookingLabelSurvivesMissingOrParaphrasedPlanAndEmptyReviewer(bool paraphrased)
    {
        var expected = Enumerable.Range(0, 3).Select(i => Photo("plain-" + i)).ToArray();
        var repository = new Picer.Core.Classification.ClassificationRepository(_db);
        var replica = repository.ExportReplica();
        replica = replica with { Photos = replica.Photos.Select(photo => photo with { SuggestedIds = ["activity.cooking"], State = "succeeded" }).ToArray() };
        using var http = new HttpClient(new Responses(JsonSerializer.Serialize(new AiQueryPlan(null, null, null, null,
            paraphrased ? ["厨房"] : [], "search")), "{\"markdown\":\"没有结果\",\"photoIds\":[],\"videoIds\":[]}"));
        using var chat = new OpenAiCompatibleChatClient(new Uri("http://127.0.0.1/chat"), "", "test", http);
        var service = new AiMediaRetrievalService(_db, _people, _videos, new CloudVisionRepository(_db), chat,
            classificationSource: _ => Task.FromResult<Picer.Core.Classification.ClassificationReplica?>(replica));
        var answer = await service.AskAsync("查找烹饪的照片", false, DateTime.Today);
        Assert.Equal(expected.Select(photo => photo.Id).Order(), answer.Photos.Select(photo => photo.Id).Order());
        Assert.All(answer.Photos, photo => Assert.Contains("模型建议", answer.PhotoRecallSource(photo.Id)));
        Assert.DoesNotContain("没有结果", answer.Markdown);
    }

    [Fact]
    public async Task MixedClassificationAndTextRequireEveryConditionAcrossEvidenceChannels()
    {
        var correct = Photo("红色围裙"); var onlyLabel = Photo("plain"); Photo("红色围裙-only");
        var replica = new Picer.Core.Classification.ClassificationRepository(_db).ExportReplica();
        replica = replica with { Photos = replica.Photos.Select(photo => photo with {
            SuggestedIds = photo.ImageId == correct.Id || photo.ImageId == onlyLabel.Id ? ["activity.cooking"] : [], State = "succeeded" }).ToArray() };
        using var http = new HttpClient(new Responses(JsonSerializer.Serialize(new AiQueryPlan(null, null, null, null, ["烹饪", "红色围裙"], "search")), "null"));
        using var chat = new OpenAiCompatibleChatClient(new Uri("http://127.0.0.1/chat"), "", "test", http);
        var service = new AiMediaRetrievalService(_db, _people, _videos, new CloudVisionRepository(_db), chat,
            classificationSource: _ => Task.FromResult<Picer.Core.Classification.ClassificationReplica?>(replica));
        var answer = await service.AskAsync("烹饪穿红色围裙的照片", false, DateTime.Today);
        Assert.Equal(correct.Id, Assert.Single(answer.Photos).Id);
    }

    [Fact]
    public async Task IndexMatchesBeyondOldLimitsSurviveBoundedModelSample()
    {
        for (var i = 0; i < 205; i++) Photo("cooking-fixture-" + i);
        var answer = await Ask("cooking-fixture", "{\"markdown\":\"仅一张\",\"photoIds\":[1],\"videoIds\":[]}", keywords: ["cooking-fixture"]);
        Assert.Equal(205, answer.Photos.Count);
        Assert.Contains("205", answer.ConfirmedMarkdown);
    }

    [Fact]
    public async Task AlbumAndVideoManualTagNamesParticipateInQuestionRecall()
    {
        var photo = Photo("neutral"); var video = Video("neutral");
        using (var command = _db.Command("""
            INSERT INTO albums(name,created_utc) VALUES('烹饪课堂',$now);
            INSERT INTO album_images(album_id,image_id,added_utc) VALUES(last_insert_rowid(),$image,$now);
            INSERT INTO tags(name,created_utc) VALUES('烹饪课堂',$now);
            """))
        { command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o")); command.Parameters.AddWithValue("$image", photo.Id); command.ExecuteNonQuery(); }
        using var tag = _db.Command("SELECT id FROM tags WHERE name=$name"); tag.Parameters.AddWithValue("$name", "烹饪课堂");
        new VideoSearchMetadata(_db).SetTag(video.Id, (long)tag.ExecuteScalar()!, true);
        var answer = await Ask("找烹饪课堂的照片和视频", keywords: ["烹饪课堂"]);
        Assert.Equal(photo.Id, Assert.Single(answer.Photos).Id);
        Assert.Equal(video.Id, Assert.Single(answer.Videos).Id);
    }

    [Fact]
    public async Task LocationFallbackCannotBypassVisualTermsAndUnknownPlaceNeedsTextEvidence()
    {
        var correct = Photo("上海-烹饪"); Photo("上海-only"); Photo("烹饪-elsewhere");
        var answer = await Ask("上海烹饪的照片", location: "上海", keywords: ["烹饪"]);
        Assert.Equal(correct.Id, Assert.Single(answer.Photos).Id);
        var unknown = await Ask("远山小村烹饪的照片", location: "远山小村", keywords: ["烹饪"]);
        Assert.Empty(unknown.Photos);
    }

    [Fact]
    public async Task ClassificationRecallHonorsPeopleIntersectionAndConflictingGps()
    {
        var first = _people.Create("fixture-first");
        var second = _people.Create("fixture-second");
        var correct = Photo("plain-a", "31.2,121.5", first.Id, second.Id);
        Photo("plain-b", "35.6,139.7", first.Id, second.Id);
        Photo("plain-c", "31.2,121.5", first.Id);
        var replica = new Picer.Core.Classification.ClassificationRepository(_db).ExportReplica();
        replica = replica with { Photos = replica.Photos.Select(photo => photo with { ConfirmedIds = ["scene.outdoor"] }).ToArray() };
        using var http = new HttpClient(new Responses(JsonSerializer.Serialize(new AiQueryPlan(null, "上海", null, null, ["户外"], "search")), "null"));
        using var chat = new OpenAiCompatibleChatClient(new Uri("http://127.0.0.1/chat"), "", "test", http);
        var service = new AiMediaRetrievalService(_db, _people, _videos, new CloudVisionRepository(_db), chat,
            classificationSource: _ => Task.FromResult<Picer.Core.Classification.ClassificationReplica?>(replica));
        var answer = await service.AskAsync("fixture-first 和 fixture-second 在上海户外的照片", false, DateTime.Today);
        Assert.Equal(correct.Id, Assert.Single(answer.Photos).Id);
        Assert.Contains("分类标签", answer.PhotoRecallSource(correct.Id));
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-ai-review-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly PeopleService _people;
    private readonly VideoRepository _videos;
    private readonly long _source;

    public AiMediaReviewRegressionTests()
    {
        _db = new(Path.Combine(_root, "index.db")); _people = new(_db); _videos = new(_db);
        _source = _db.AddSource(_root).Id;
    }

    private ImageRecord Photo(string name, string? gps = null, params long[] people)
    {
        var image = _db.UpsertImage(new() { SourceId = _source, Path = Path.Combine(_root, name + ".jpg"),
            FileSize = 1, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active, Gps = gps });
        foreach (var person in people)
        {
            using var face = _db.Command("""
                INSERT INTO detected_faces(image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc)
                VALUES($image,'test',$person,0,0,1,1,1,$now) RETURNING id
                """);
            face.Parameters.AddWithValue("$image", image.Id); face.Parameters.AddWithValue("$person", person);
            face.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            _people.ConfirmFace((long)face.ExecuteScalar()!, person);
        }
        return image;
    }

    private VideoRecord Video(string name, string? gps = null, params long[] people)
    {
        var path = Path.Combine(_root, name + ".mp4");
        _videos.UpsertRemote(new(0, _source, path, 1, DateTime.UtcNow, 1, 1, 1, null, gps, "active"), _source);
        var video = _videos.GetAll().Single(value => value.Path == path);
        foreach (var person in people)
        {
            using var face = _db.Command("""
                INSERT OR IGNORE INTO video_keyframes(video_id,timestamp_seconds,path,created_utc) VALUES($video,0,'cached.jpg',$now);
                INSERT INTO video_detected_faces(video_id,keyframe_id,timestamp_seconds,ordinal,model_version,box_x,box_y,box_width,box_height,confidence,created_utc,assigned_person_id,assignment_state)
                SELECT $video,id,0,$person,'test',0,0,1,1,1,$now,$person,'confirmed' FROM video_keyframes WHERE video_id=$video
                """);
            face.Parameters.AddWithValue("$video", video.Id); face.Parameters.AddWithValue("$person", person);
            face.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o")); face.ExecuteNonQuery();
        }
        return video;
    }

    private async Task<AiMediaAnswer> Ask(string question, string review = "null", string? location = null, string[]? locationTerms = null, string? plannerPerson = null, string[]? keywords = null, IAiSemanticRecall? semantic = null)
    {
        using var http = new HttpClient(new Responses(JsonSerializer.Serialize(new AiQueryPlan(plannerPerson, location, null, null, keywords ?? [], "search", locationTerms)), review));
        using var chat = new OpenAiCompatibleChatClient(new Uri("http://127.0.0.1/chat"), "", "test", http);
        return await new AiMediaRetrievalService(_db, _people, _videos, new CloudVisionRepository(_db), chat, semanticRecall: semantic).AskAsync(question, true, DateTime.Today);
    }

    [Fact]
    public async Task LocationTextCannotOverrideConflictingGpsForPhotosOrVideos()
    {
        var correct = Photo("上海本地", "31.2,121.5");
        Photo("上海旅行记录", "32.7373972,129.8673556");
        Video("上海旅行记录", "32.7373972,129.8673556");
        var answer = await Ask("上海的照片和视频", location: "上海");
        Assert.Equal(correct.Id, Assert.Single(answer.Photos).Id);
        Assert.Empty(answer.Videos);
    }

    [Fact]
    public async Task SemanticRecallCannotEscapePeopleIntersectionAndLocation()
    {
        var first = _people.Create("测试甲"); var second = _people.Create("测试乙");
        var valid = Photo("plain", "31.2,121.5", first.Id, second.Id);
        var wrongPeople = Photo("other", "31.2,121.5", first.Id);
        var wrongPlace = Photo("elsewhere", "32.7373972,129.8673556", first.Id, second.Id);
        var semantic = new RecallStub([wrongPeople.Id, wrongPlace.Id, valid.Id]);
        var answer = await Ask("测试甲和测试乙在上海森林中的照片", location: "上海", keywords: ["森林"], semantic: semantic);
        Assert.Equal(valid.Id, Assert.Single(answer.Photos).Id);
        Assert.Equal(valid.Id, Assert.Single(semantic.Allowed!));
    }

    [Fact]
    public async Task StructuredQueryDoesNotInvokeSemanticRecall()
    {
        Photo("city", "31.2,121.5");
        var semantic = new RecallStub([]);
        await Ask("上海照片", location: "上海", semantic: semantic);
        Assert.Null(semantic.Allowed);
    }

    private sealed class RecallStub(long[] ids) : IAiSemanticRecall
    {
        public IReadOnlySet<long>? Allowed { get; private set; }
        public Task<AiSemanticRecallResult> SearchAsync(string query, IReadOnlySet<long> allowed, CancellationToken token)
        { Allowed = allowed; return Task.FromResult(new AiSemanticRecallResult(ids, "语义候选")); }
    }

    [Fact]
    public async Task MultipleAliasesRequireAllPeopleForPhotosAndVideos()
    {
        var first = _people.Create("甲人物", ["小甲"]); var second = _people.Create("乙人物", ["小乙"]);
        Photo("a", null, first.Id); Photo("b", null, second.Id); var both = Photo("ab", null, first.Id, second.Id);
        Video("a", null, first.Id); Video("b", null, second.Id); var bothVideo = Video("ab", null, first.Id, second.Id);
        var answer = await Ask("找一下小甲和小乙的合影", plannerPerson: "不存在的人物");
        Assert.Equal(both.Id, Assert.Single(answer.Photos).Id); Assert.Equal(bothVideo.Id, Assert.Single(answer.Videos).Id);
        Assert.Equal(first.DisplayName, answer.Plan.PersonName);
    }

    [Theory]
    [InlineData("找一下安娜的照片", "安娜")]
    [InlineData("Anna的照片", "Anna")]
    [InlineData("牟的照片", "牟")]
    [InlineData("长安的风景照片", null)]
    [InlineData("Annual report", null)]
    [InlineData("自我介绍的照片", null)]
    public void MentionMatchingUsesLongestNamesAndRejectsWordSubstrings(string question, string? expected)
    {
        foreach (var name in new[] { "安", "安娜", "Ann", "Anna", "牟", "我" }) _people.Create(name);
        var mentions = _people.FindMentions(question);
        if (expected is null) Assert.Empty(mentions);
        else Assert.Equal(expected, Assert.Single(mentions).Person.DisplayName);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"markdown\":\"\",\"photoIds\":[],\"videoIds\":[]}")]
    [InlineData("{\"markdown\":\"invalid\",\"photoIds\":\"wrong type\",\"videoIds\":[]}")]
    public async Task IncompleteReviewerFallsBackToCandidateEvidence(string review)
    {
        var photo = Photo("candidate"); var video = Video("candidate");
        var answer = await Ask("查看媒体", review);
        Assert.Equal(photo.Id, Assert.Single(answer.Photos).Id); Assert.Equal(video.Id, Assert.Single(answer.Videos).Id);
        Assert.Contains("AI 复核未返回完整结果", answer.Markdown);
    }

    [Fact]
    public async Task PlannerCannotReintroducePersonRejectedByBoundaryMatching()
    {
        var person = _people.Create("安"); Photo("person", null, person.Id); var unrelated = Photo("风景");
        var answer = await Ask("长安的风景照片", plannerPerson: "安");
        Assert.Null(answer.Plan.PersonName); Assert.Contains(answer.Photos, photo => photo.Id == unrelated.Id);
    }

    [Fact]
    public async Task GpsAndLocationAliasesRecallBothMediaKindsWithoutRequiringCityInDescription()
    {
        var photo = Photo("gps-only", "31.2,121.5"); var video = Video("gps-only", "31.2,121.5");
        var alias = Video("魔都游览"); var rewrittenAlias = Video("申城游览");
        var unrelated = Video("outside", "39.9,116.4");
        var answer = await Ask("上海的照片和视频", location: "上海", locationTerms: ["魔都"]);
        Assert.Contains(answer.Photos, value => value.Id == photo.Id);
        Assert.Contains(answer.Videos, value => value.Id == video.Id);
        Assert.Contains(answer.Videos, value => value.Id == alias.Id);
        Assert.Contains(answer.Videos, value => value.Id == rewrittenAlias.Id);
        Assert.DoesNotContain(answer.Videos, value => value.Id == unrelated.Id);
    }

    [Fact]
    public async Task ContradictoryModelExplanationCannotReplaceConfirmedCountsOrIdentity()
    {
        var person = _people.Create("甲人物"); var photo = Photo("confirmed", null, person.Id);
        var review = JsonSerializer.Serialize(new { markdown = "找到 999 张乙人物的照片。", photoIds = new[] { photo.Id }, videoIds = Array.Empty<long>() });
        var answer = await Ask("甲人物的 confirmed 照片", review, keywords: ["confirmed"]);
        Assert.Contains("照片 **1 张**", answer.ConfirmedMarkdown); Assert.Contains("甲人物", answer.ConfirmedMarkdown);
        Assert.DoesNotContain("999", answer.ConfirmedMarkdown); Assert.DoesNotContain("乙人物", answer.ConfirmedMarkdown);
        Assert.Null(answer.AiExplanation);
        Assert.DoesNotContain("乙人物", answer.Markdown);
        Assert.DoesNotContain("999", answer.Markdown);
        Assert.Equal(answer.ConfirmedMarkdown, answer.Markdown);
    }

    [Fact]
    public async Task StructuredPersonAndLocationQueryNeedsOnlyPlannerAndKeepsTextFallback()
    {
        var self = _people.Create("我"); var other = _people.Create("牟");
        var gps = Photo("gps", "31.2,121.5", self.Id, other.Id);
        var text = Photo("上海文字地点", null, self.Id, other.Id);
        Photo("上海无目标人物", "31.2,121.5", self.Id);
        var handler = new Responses(JsonSerializer.Serialize(new AiQueryPlan("张", "上海", null, null, [], "search")),
            "{\"markdown\":\"错误的人物解释\",\"photoIds\":[],\"videoIds\":[]}");
        using var http = new HttpClient(handler);
        using var chat = new OpenAiCompatibleChatClient(new Uri("http://127.0.0.1/chat"), "", "test", http);
        var answer = await new AiMediaRetrievalService(_db, _people, _videos, new CloudVisionRepository(_db), chat)
            .AskAsync("找一下我在上海和牟一起的照片", false, DateTime.Today);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(new[] { gps.Id, text.Id }.Order(), answer.Photos.Select(photo => photo.Id).Order());
        Assert.Contains("我、牟", answer.ConfirmedMarkdown);
        Assert.Null(answer.AiExplanation);
        Assert.DoesNotContain("错误的人物解释", answer.Markdown);
    }

    [Fact]
    public async Task PersonSetFilterKeepsSearchPagingAndCountConsistent()
    {
        var person = _people.Create("目标人物");
        var first = Photo("first", null, person.Id);
        var second = Photo("second", null, person.Id);
        var missing = Photo("missing", null, person.Id);
        _db.MarkMissing([missing.Id]);
        Photo("unassigned");
        using (var face = _db.Command("""
            INSERT INTO detected_faces(image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc)
            VALUES($image,'test',999,0,0,1,1,1,$now) RETURNING id
            """))
        {
            face.Parameters.AddWithValue("$image", first.Id);
            face.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            _people.ConfirmFace((long)face.ExecuteScalar()!, person.Id);
        }
        var filters = new SearchFilters { PersonId = person.Id, IncludeMissing = false, SortBy = SortField.FileName, SortDir = SortDirection.Ascending };
        Assert.Equal(new[] { first.Id, second.Id }, IndexSearchEngine.Search(_db, filters).Select(image => image.Id));
        var page = await IndexSearchEngine.SearchPagedAsync(_db, filters, 1);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(first.Id, Assert.Single(page.Items).Id);
        var next = IndexSearchEngine.SearchPaged(_db, filters, 1, page.NextCursor);
        Assert.Equal(2, next.TotalCount);
        Assert.Equal(second.Id, Assert.Single(next.Items).Id);
    }

    [Fact]
    public async Task EmptyVerifiedResultSuppressesUnsupportedModelHitClaimAndExplanation()
    {
        var photo = Photo("candidate");
        var answer = await Ask("查看媒体", "{\"markdown\":\"找到 999 张照片\",\"photoIds\":[999999],\"videoIds\":[]}");
        Assert.Equal(photo.Id, Assert.Single(answer.Photos).Id); Assert.Empty(answer.Videos);
        Assert.Contains("1 张", answer.ConfirmedMarkdown.Replace("**", ""));
        Assert.DoesNotContain("999", answer.Markdown);
    }

    [Fact]
    public async Task NoIndexEvidenceCannotBeCreatedByReviewer()
    {
        var answer = await Ask("不存在的内容", "{\"markdown\":\"找到 999 张照片\",\"photoIds\":[999999],\"videoIds\":[]}", keywords: ["不存在"]);
        Assert.Empty(answer.Photos); Assert.Empty(answer.Videos); Assert.Null(answer.AiExplanation);
        Assert.Contains("未找到可返回的匹配内容", answer.ConfirmedMarkdown);
    }

    [Fact]
    public async Task ReviewerNetworkFailurePreservesIndexedEvidence()
    {
        var photo = Photo("烹饪");
        using var http = new HttpClient(new Responses(JsonSerializer.Serialize(new AiQueryPlan(null, null, null, null, ["烹饪"], "search")), "null", true));
        using var chat = new OpenAiCompatibleChatClient(new Uri("http://127.0.0.1/chat"), "", "test", http);
        var answer = await new AiMediaRetrievalService(_db, _people, _videos, new CloudVisionRepository(_db), chat)
            .AskAsync("烹饪照片", false, DateTime.Today);
        Assert.Equal(photo.Id, Assert.Single(answer.Photos).Id);
        Assert.Contains("模型复核暂不可用", answer.Markdown);
    }

    public void Dispose()
    {
        _db.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true);
    }

    private sealed class Responses(string plan, string review, bool failReview = false) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++_calls > 1 && failReview) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { message = new { content = _calls == 1 ? plan : review } } } }), Encoding.UTF8, "application/json") });
        }
    }
}
