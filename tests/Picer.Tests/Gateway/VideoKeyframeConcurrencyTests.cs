using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Thumbnails;
using Picer.Core.Video;
using Picer.IndexService;

namespace Picer.Tests;

public sealed class VideoKeyframeConcurrencyTests
{
    [Fact]
    public async Task KeyframeSaveUsesIndependentConnectionDuringAnotherRequestTransaction()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-keyframe-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new PicerDatabase(Path.Combine(root, "index.db"));
            var media = Path.Combine(root, "video.mp4"); File.WriteAllBytes(media, [1, 2, 3]);
            var repository = new VideoRepository(database);
            var video = repository.Upsert(database.AddSource(root).Id, media);
            var handlers = new IndexApiHandlers(database, new ThumbnailService(Path.Combine(root, "thumbs")), repository);
            Task<IReadOnlyList<VideoKeyframe>> save;
            using (var transaction = database.BeginTransaction())
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                save = Task.Run(() =>
                {
                    entered.TrySetResult();
                    return handlers.SaveGeneratedKeyframes(video.Id, [new VideoKeyframe(0, "synthetic-frame.jpg")]);
                });
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(100);
                transaction.Commit();
            }
            var result = await save.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(Assert.Single(result).Id > 0);
            Assert.Single(repository.GetKeyframes(video.Id, existingOnly: false));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
