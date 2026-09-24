using System.Net;
using System.Net.Http;
using System.Text;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Search;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class AiMediaRetrievalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-ai-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;

    public AiMediaRetrievalTests()
    {
        Directory.CreateDirectory(_root);
        _database = new PicerDatabase(Path.Combine(_root, "ai.db"));
    }

    [Fact]
    public async Task ExplicitConfirmedPersonOverridesPlannerNameAndReviewerCannotDropMatch()
    {
        var source = _database.AddSource(_root);
        var path = Path.Combine(_root, "mou.jpg");
        File.WriteAllBytes(path, [1, 2, 3]);
        var image = _database.UpsertImage(new ImageRecord
        {
            SourceId = source.Id, Path = path, FileSize = 3,
            ModifiedUtc = File.GetLastWriteTimeUtc(path), Status = ImageStatus.Active,
            IndexedUtc = DateTime.UtcNow
        });
        var people = new PeopleService(_database);
        var mou = people.Create("牟");
        using (var insert = _database.Command("""
            INSERT INTO detected_faces(image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc)
            VALUES($image,'test',0,0.1,0.1,0.3,0.3,0.99,$now); SELECT last_insert_rowid();
            """))
        {
            insert.Parameters.AddWithValue("$image", image.Id);
            insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            people.ConfirmFace((long)insert.ExecuteScalar()!, mou.Id);
        }

        using var http = new HttpClient(new PlannerAndEmptyReviewerHandler());
        using var chat = new OpenAiCompatibleChatClient(
            new Uri("http://127.0.0.1/chat"), "", "test", http);
        var service = new AiMediaRetrievalService(_database, people, new VideoRepository(_database),
            new CloudVisionRepository(_database), chat);

        var answer = await service.AskAsync("牟的照片", includeVideos: false, DateTime.Today);

        Assert.Equal(mou.Id, answer.Plan.PersonName is not null ? people.FindByName(answer.Plan.PersonName)?.Id : null);
        Assert.Contains(answer.Photos, value => value.Id == image.Id);
        Assert.Contains("牟", answer.Markdown);
    }

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class PlannerAndEmptyReviewerHandler : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _calls++;
            var content = _calls == 1
                ? "{\"choices\":[{\"message\":{\"content\":\"{\\\"personName\\\":\\\"张\\\",\\\"location\\\":null,\\\"startDate\\\":null,\\\"endDate\\\":null,\\\"keywords\\\":[],\\\"intent\\\":\\\"search\\\"}\"}}]}"
                : "{\"choices\":[{\"message\":{\"content\":\"{\\\"markdown\\\":\\\"未确认\\\",\\\"photoIds\\\":[],\\\"videoIds\\\":[] }\"}}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }
    }
}
