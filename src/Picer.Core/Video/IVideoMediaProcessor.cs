namespace Picer.Core.Video;

public interface IVideoMediaProcessor
{
    Task<VideoProbeResult> ProbeAsync(VideoRecord video, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VideoKeyframe>> ExtractAsync(VideoRecord video, CancellationToken cancellationToken = default);
    Task<string> GetOrCreatePosterAsync(VideoRecord video, CancellationToken cancellationToken = default);
}
