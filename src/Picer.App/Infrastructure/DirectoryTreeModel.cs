using Picer.Core.Indexing;
using Picer.Core.Identity;

namespace Picer.App.Infrastructure;

internal sealed record DirectoryTreeModel(string Name, string Path)
{
    public List<DirectoryTreeModel> Children { get; } = [];

    public static IReadOnlyList<DirectoryTreeModel> Build(IEnumerable<string> paths, CancellationToken token = default)
    {
        var nodes = new Dictionary<string, DirectoryTreeModel>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<DirectoryTreeModel>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            var normalized = DirectoryMediaImport.Normalize(path);
            var slash = normalized.LastIndexOf('/');
            if (slash <= 0) continue;
            var directory = normalized[..slash];
            DirectoryTreeModel? parent = null;
            var offset = directory.StartsWith('/') ? 1 : 0;
            var hiddenSegments = PublishedMediaPath.TryUnqualify(directory, out _, out var original)
                ? original.StartsWith(@"\\", StringComparison.Ordinal) ? 4 : 2
                : 0;
            var segmentIndex = 0;
            foreach (var component in directory[offset..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                offset = directory.IndexOf(component, offset, StringComparison.Ordinal) + component.Length;
                if (segmentIndex++ < hiddenSegments) continue;
                var prefix = directory[..offset];
                if (!nodes.TryGetValue(prefix, out var node))
                {
                    node = new(component, prefix); nodes.Add(prefix, node);
                    if (parent is null) roots.Add(node); else parent.Children.Add(node);
                }
                parent = node;
            }
        }
        foreach (var node in nodes.Values) node.Children.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        roots.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        return roots;
    }
}
