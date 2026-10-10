namespace Picer.Core.Gateway;

public enum VideoPlaybackMode { Automatic, PreferStreaming, Download }
public enum VideoPlayerKind { Unknown, Vlc, Mpv }
public static class VideoPlayerPolicy
{
    public static VideoPlayerKind Identify(string executable, string? productName)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        if (name.Equals("vlc", StringComparison.OrdinalIgnoreCase) && productName?.Contains("VLC", StringComparison.OrdinalIgnoreCase) == true) return VideoPlayerKind.Vlc;
        if (name.Equals("mpv", StringComparison.OrdinalIgnoreCase) && productName?.Contains("mpv", StringComparison.OrdinalIgnoreCase) == true) return VideoPlayerKind.Mpv;
        return VideoPlayerKind.Unknown;
    }
    public static bool ShouldStream(VideoPlaybackMode mode, VideoPlayerKind player, bool serviceSupportsStreaming, bool hasExecutable) =>
        serviceSupportsStreaming && hasExecutable && mode != VideoPlaybackMode.Download && (mode == VideoPlaybackMode.PreferStreaming || player != VideoPlayerKind.Unknown);
}
