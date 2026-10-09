namespace Picer.Core.Video;

/// <summary>A fresh generation never overwrites frames still referenced by another content record.</summary>
public sealed class VideoKeyframeGeneration : IDisposable
{
    private readonly string _root;
    private readonly string _published;
    public string Workspace { get; }

    public VideoKeyframeGeneration(string root)
    {
        _root = Path.GetFullPath(root);
        var id = Guid.NewGuid().ToString("N");
        Workspace = Path.Combine(_root, ".pending-" + id);
        _published = Path.Combine(_root, "generation-" + id);
        Directory.CreateDirectory(Workspace);
    }

    public IReadOnlyList<VideoKeyframe> Publish(IReadOnlyList<VideoKeyframe> frames)
    {
        var prefix = Workspace + Path.DirectorySeparatorChar;
        var result = frames.Select(frame =>
        {
            var path = Path.GetFullPath(frame.Path);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("keyframe_generation_outside_workspace");
            return frame with { Path = Path.Combine(_published, Path.GetRelativePath(Workspace, path)) };
        }).ToArray();
        Directory.Move(Workspace, _published);
        return result;
    }

    public void Dispose()
    {
        var workspace = Path.GetFullPath(Workspace);
        if (!workspace.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("keyframe_generation_outside_root");
        if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
    }
}
