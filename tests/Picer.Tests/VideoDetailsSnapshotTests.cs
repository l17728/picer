using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class VideoDetailsSnapshotTests
{
    [Fact]
    public void SelectedVideoReadContainsOnlyItsOwnMetadataAndDoesNotMutateOtherVideos()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-video-details-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var db = new PicerDatabase(Path.Combine(root, "index.db"));
            var source = db.AddSource(root);
            var videos = new VideoRepository(db);
            var a = Path.Combine(root, "a.mp4"); var b = Path.Combine(root, "b.mp4");
            File.WriteAllBytes(a, [1, 2, 3]); File.WriteAllBytes(b, [4, 5, 6]);
            var first = videos.Upsert(source.Id, a); var second = videos.Upsert(source.Id, b);
            videos.SaveTranscript(first.Id, "fixture", "first transcript");
            videos.SaveTranscript(second.Id, "fixture", "second transcript");
            videos.SaveKeyframes(first.Id, [new VideoKeyframe(1, "first-frame")]);
            videos.SaveKeyframes(second.Id, [new VideoKeyframe(2, "second-frame")]);
            var details = db.ReadSnapshot(reader => VideoDetailsSnapshot.Read(reader, first.Id, "fixture"));
            Assert.NotNull(details);
            Assert.Equal(first.Id, details.Video.Id);
            Assert.Equal("first transcript", details.Transcript);
            Assert.Equal("first-frame", Assert.Single(details.Frames).Path);
            Assert.Equal("second transcript", videos.GetTranscript(second.Id));
            Assert.Equal("second-frame", Assert.Single(videos.GetKeyframes(second.Id, existingOnly: false)).Path);
            Assert.Null(VideoDetailsSnapshot.Read(db, long.MaxValue, "fixture"));
        }
        finally { Directory.Delete(root, true); }
    }
}
