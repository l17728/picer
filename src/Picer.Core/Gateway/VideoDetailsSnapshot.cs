using Picer.Core.Data;
using Picer.Core.Video;

namespace Picer.Core.Gateway;

public sealed record VideoDetailsSnapshot(string Epoch, long Revision, VideoRecord Video,
    string? Transcript, string? Summary, IReadOnlyList<VideoKeyframe> Frames,
    IReadOnlyList<VideoFaceTimelineRecord> Timeline)
{
    public static VideoDetailsSnapshot? Read(PicerDatabase database, long id, string model)
    {
        var repository = new VideoRepository(database);
        var video = repository.Get(id);
        if (video is null || video.Status != "active") return null;
        string epoch; long revision;
        using (var command = database.Command("SELECT epoch,metadata_revision FROM mirror_revision WHERE id=1"))
        using (var reader = command.ExecuteReader()) { reader.Read(); epoch = reader.GetString(0); revision = reader.GetInt64(1); }
        return new(epoch, revision, video, repository.GetTranscript(id), repository.GetSummary(id),
            repository.GetKeyframes(id, existingOnly: false).ToArray(), VideoFaceTimelineService.ReadTimeline(database, id, model));
    }
}
