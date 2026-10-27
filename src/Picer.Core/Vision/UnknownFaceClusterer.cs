namespace Picer.Core.Vision;

public sealed record CandidateClusteringResult(int Considered, int ClusterCount, int ClusteredFaces, int DeferredFaces);

/// <summary>Groups unknown faces into review-only candidates without assigning identities.</summary>
public sealed class UnknownFaceClusterer(VisionAnalysisRepository repository)
{
    public CandidateClusteringResult Rebuild(double cosineThreshold = 0.62, int maximumFaces = int.MaxValue,
        IProgress<(string Stage, int Completed, int Total)>? progress = null, string? modelVersion = null,
        string? expectedEpoch = null, CancellationToken cancellationToken = default)
    {
        if (cosineThreshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(cosineThreshold));
        if (maximumFaces <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFaces));
        var all = repository.GetEmbeddingsForMatching(modelVersion)
            .Where(face => face.AssignmentState is null)
            .ToArray();
        var selectedVersion = modelVersion ?? all.GroupBy(face => face.ModelVersion)
            .OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault();
        if (selectedVersion is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            repository.ReplaceCandidateClusters("none", [], expectedEpoch);
            return new CandidateClusteringResult(0, 0, 0, 0);
        }
        var faces = all.Where(face => face.ModelVersion == selectedVersion).Take(maximumFaces).ToArray();
        var owners = new Dictionary<long, string>();
        using (var ownership = repository.Database.Command("""
            SELECT DISTINCT f.image_id,COALESCE(o.owner_key,'local') FROM detected_faces f
            LEFT JOIN sync_image_owners o ON o.image_id=f.image_id WHERE f.model_version=$model
            """))
        {
            ownership.Parameters.AddWithValue("$model", selectedVersion);
            using var rows = ownership.ExecuteReader();
            while (rows.Read()) owners[rows.GetInt64(0)] = rows.GetString(1);
        }
        faces = faces.Where(face => owners.TryGetValue(face.ImageId, out var owner) &&
            !owner.StartsWith("ambiguous-source:", StringComparison.Ordinal)).ToArray();
        var deferred = all.Length - faces.Length;
        var sets = Enumerable.Range(0, faces.Length).Select(index => new ClusterSet(index, faces[index].ImageId)).ToArray();

        progress?.Report(("构建近邻索引", 0, faces.Length));
        var index = new AngularNeighborIndex(faces.Select(face => face.Embedding).ToArray());
        progress?.Report(("比较相似人脸", 0, faces.Length));
        var edges = new List<(int Left, int Right, double Score)>();
        for (var left = 0; left < faces.Length; left++)
        {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var right in index.FindWithin(left, cosineThreshold))
        {
            if (right <= left) continue;
            if (faces[left].ImageId == faces[right].ImageId) continue;
            if (owners.GetValueOrDefault(faces[left].ImageId) != owners.GetValueOrDefault(faces[right].ImageId)) continue;
            var score = Cosine(faces[left].Embedding, faces[right].Embedding);
            if (score >= cosineThreshold) edges.Add((left, right, score));
        }
            if ((left + 1) % 100 == 0 || left + 1 == faces.Length)
                progress?.Report(("比较相似人脸", left + 1, faces.Length));
        }

        var merged = 0;
        foreach (var edge in edges.OrderByDescending(value => value.Score))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leftRoot = Find(sets, edge.Left);
            var rightRoot = Find(sets, edge.Right);
            if (leftRoot == rightRoot || sets[leftRoot].ImageIds.Overlaps(sets[rightRoot].ImageIds)) continue;
            Union(sets, leftRoot, rightRoot);
            if (++merged % 500 == 0) progress?.Report(("合并候选组", merged, Math.Max(1, edges.Count)));
        }

        var drafts = Enumerable.Range(0, faces.Length)
            .GroupBy(index => Find(sets, index))
            .Where(group => group.Count() >= 2)
            .Select(group =>
            {
                var indices = group.ToArray();
                var prototype = Mean(indices.Select(index => faces[index].Embedding));
                return new CandidateClusterDraft(indices
                    .Select(index => (faces[index].FaceId, Cosine(faces[index].Embedding, prototype)))
                    .OrderByDescending(value => value.Item2).ToArray());
            }).ToArray();
        progress?.Report(("保存候选组", 0, Math.Max(1, drafts.Length)));
        cancellationToken.ThrowIfCancellationRequested();
        repository.ReplaceCandidateClusters(selectedVersion, drafts, expectedEpoch);
        progress?.Report(("保存候选组", drafts.Length, Math.Max(1, drafts.Length)));
        return new CandidateClusteringResult(faces.Length, drafts.Length,
            drafts.Sum(cluster => cluster.Members.Count), deferred);
    }

    private static int Find(ClusterSet[] sets, int index)
    {
        while (sets[index].Parent != index)
        {
            sets[index].Parent = sets[sets[index].Parent].Parent;
            index = sets[index].Parent;
        }
        return index;
    }

    private static void Union(ClusterSet[] sets, int leftRoot, int rightRoot)
    {
        if (sets[leftRoot].Rank < sets[rightRoot].Rank) (leftRoot, rightRoot) = (rightRoot, leftRoot);
        sets[rightRoot].Parent = leftRoot;
        sets[leftRoot].ImageIds.UnionWith(sets[rightRoot].ImageIds);
        if (sets[leftRoot].Rank == sets[rightRoot].Rank) sets[leftRoot].Rank++;
    }

    private static float[] Mean(IEnumerable<float[]> vectors)
    {
        var items = vectors.ToArray();
        var length = items.Min(vector => vector.Length);
        var result = new float[length];
        foreach (var vector in items)
            for (var index = 0; index < length; index++) result[index] += vector[index];
        for (var index = 0; index < length; index++) result[index] /= items.Length;
        return result;
    }

    private static double Cosine(float[] left, float[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var index = 0; index < length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }
        return length == 0 || leftNorm <= 0 || rightNorm <= 0 ? -1 : dot / Math.Sqrt(leftNorm * rightNorm);
    }

    private sealed class ClusterSet(int parent, long imageId)
    {
        public int Parent { get; set; } = parent;
        public int Rank { get; set; }
        public HashSet<long> ImageIds { get; } = [imageId];
    }

    /// <summary>
    /// Exact radius searches for cosine-normalized face vectors. A vantage-point tree avoids
    /// scanning every pair while retaining the existing cosine-threshold behavior.
    /// </summary>
    private sealed class AngularNeighborIndex
    {
        private readonly float[][] _vectors;
        private readonly Node? _root;

        public AngularNeighborIndex(float[][] vectors)
        {
            _vectors = vectors;
            _root = Build(Enumerable.Range(0, vectors.Length).ToArray());
        }

        public IEnumerable<int> FindWithin(int target, double minimumCosine)
        {
            if (_root is null) yield break;
            var radius = Math.Acos(Math.Clamp(minimumCosine, -1, 1));
            var matches = new List<int>();
            Search(_root, target, radius, matches);
            foreach (var match in matches) yield return match;
        }

        private Node? Build(int[] items)
        {
            if (items.Length == 0) return null;
            var vantage = items[^1];
            if (items.Length == 1) return new Node(vantage, 0, null, null);

            var distances = items[..^1]
                .Select(index => (Index: index, Distance: AngularDistance(vantage, index)))
                .OrderBy(value => value.Distance)
                .ToArray();
            var median = distances.Length / 2;
            var threshold = distances[median].Distance;
            return new Node(vantage, threshold,
                Build(distances.Take(median + 1).Select(value => value.Index).ToArray()),
                Build(distances.Skip(median + 1).Select(value => value.Index).ToArray()));
        }

        private void Search(Node node, int target, double radius, List<int> matches)
        {
            var distance = AngularDistance(target, node.Index);
            if (distance <= radius + 1e-12) matches.Add(node.Index);
            if (node.Near is not null && distance - radius <= node.Threshold)
                Search(node.Near, target, radius, matches);
            if (node.Far is not null && distance + radius > node.Threshold)
                Search(node.Far, target, radius, matches);
        }

        private double AngularDistance(int left, int right) =>
            Math.Acos(Math.Clamp(Cosine(_vectors[left], _vectors[right]), -1, 1));

        private sealed record Node(int Index, double Threshold, Node? Near, Node? Far);
    }
}
