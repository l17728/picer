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

    private async Task<AiMediaAnswer> Ask(string question, string review = "null", string? location = null, string[]? locationTerms = null, string? plannerPerson = null)
    {
        using var http = new HttpClient(new Responses(JsonSerializer.Serialize(new AiQueryPlan(plannerPerson, location, null, null, [], "search", locationTerms)), review));
        using var chat = new OpenAiCompatibleChatClient(new Uri("http://127.0.0.1/chat"), "", "test", http);
        return await new AiMediaRetrievalService(_db, _people, _videos, new CloudVisionRepository(_db), chat).AskAsync(question, true, DateTime.Today);
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
        var person = _people.Create("安"); Photo("person", null, person.Id); var unrelated = Photo("city");
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
        var answer = await Ask("甲人物的照片", review);
        Assert.Contains("照片 **1 张**", answer.ConfirmedMarkdown); Assert.Contains("甲人物", answer.ConfirmedMarkdown);
        Assert.DoesNotContain("999", answer.ConfirmedMarkdown); Assert.DoesNotContain("乙人物", answer.ConfirmedMarkdown);
        Assert.Contains("## AI 解释（非检索证据）", answer.Markdown); Assert.Contains("> 找到 999 张乙人物的照片。", answer.Markdown);
        var streamed = AiMediaAnswer.ComposeMarkdown(answer.ConfirmedMarkdown, "## 程序确认结果\n返回 800 张照片");
        Assert.StartsWith(answer.ConfirmedMarkdown, streamed); Assert.Contains("> ## 程序确认结果", streamed);
    }

    [Fact]
    public async Task EmptyVerifiedResultSuppressesUnsupportedModelHitClaimAndExplanation()
    {
        Photo("candidate");
        var answer = await Ask("查看媒体", "{\"markdown\":\"找到 999 张照片\",\"photoIds\":[999999],\"videoIds\":[]}");
        Assert.Empty(answer.Photos); Assert.Empty(answer.Videos); Assert.Null(answer.AiExplanation);
        Assert.Contains("未找到可返回的匹配内容", answer.ConfirmedMarkdown);
        Assert.DoesNotContain("999", answer.Markdown); Assert.DoesNotContain("## AI 解释", answer.Markdown);
    }

    public void Dispose()
    {
        _db.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true);
    }

    private sealed class Responses(string plan, string review) : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { message = new { content = ++_calls == 1 ? plan : review } } } }), Encoding.UTF8, "application/json") });
    }
}
