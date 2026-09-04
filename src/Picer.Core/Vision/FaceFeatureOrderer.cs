namespace Picer.Core.Vision;

/// <summary>Groups faces by embedding proximity and keeps faces without embeddings at the end.</summary>
public static class FaceFeatureOrderer
{
    public static IReadOnlyList<UnassignedFaceRecord> Order(
        IReadOnlyList<UnassignedFaceRecord> faces,
        IReadOnlyDictionary<long, float[]> embeddings)
    {
        if (faces.Count < 2) return faces;
        var embedded = faces.Where(face => embeddings.ContainsKey(face.FaceId)).ToArray();
        if (embedded.Length < 2) return embedded.Concat(faces.Where(face => !embeddings.ContainsKey(face.FaceId))).ToArray();

        var clusterCount = Math.Clamp((int)Math.Ceiling(Math.Sqrt(embedded.Length / 6d)), 1, 32);
        var centroids = SelectSeeds(embedded, embeddings, clusterCount);
        var assignments = new int[embedded.Length];
        for (var iteration = 0; iteration < 5; iteration++)
        {
            for (var index = 0; index < embedded.Length; index++)
                assignments[index] = BestCentroid(embeddings[embedded[index].FaceId], centroids);
            centroids = Recalculate(embedded, embeddings, assignments, centroids);
        }

        var ordered = Enumerable.Range(0, clusterCount)
            .Select(cluster => embedded.Select((face, index) => (face, index))
                .Where(value => assignments[value.index] == cluster)
                .OrderByDescending(value => Cosine(embeddings[value.face.FaceId], centroids[cluster]))
                .ThenBy(value => value.face.FaceId)
                .Select(value => value.face).ToArray())
            .Where(cluster => cluster.Length > 0)
            .OrderByDescending(cluster => cluster.Length)
            .ThenBy(cluster => cluster[0].FaceId)
            .SelectMany(cluster => cluster)
            .Concat(faces.Where(face => !embeddings.ContainsKey(face.FaceId)))
            .ToArray();
        return ordered;
    }

    private static float[][] SelectSeeds(IReadOnlyList<UnassignedFaceRecord> faces,
        IReadOnlyDictionary<long, float[]> embeddings, int count)
    {
        var seeds = new List<float[]> { embeddings[faces[0].FaceId] };
        while (seeds.Count < count)
        {
            var next = faces.Select(face => embeddings[face.FaceId])
                .OrderBy(vector => seeds.Max(seed => Cosine(vector, seed)))
                .First();
            seeds.Add(next);
        }
        return seeds.Select(vector => vector.ToArray()).ToArray();
    }

    private static int BestCentroid(float[] vector, IReadOnlyList<float[]> centroids)
    {
        var best = 0;
        var score = double.NegativeInfinity;
        for (var index = 0; index < centroids.Count; index++)
        {
            var candidate = Cosine(vector, centroids[index]);
            if (candidate <= score) continue;
            best = index;
            score = candidate;
        }
        return best;
    }

    private static float[][] Recalculate(IReadOnlyList<UnassignedFaceRecord> faces,
        IReadOnlyDictionary<long, float[]> embeddings, IReadOnlyList<int> assignments,
        IReadOnlyList<float[]> previous)
    {
        var result = new float[previous.Count][];
        for (var cluster = 0; cluster < previous.Count; cluster++)
        {
            var vectors = faces.Select((face, index) => (Vector: embeddings[face.FaceId], index))
                .Where(value => assignments[value.index] == cluster).Select(value => value.Vector).ToArray();
            if (vectors.Length == 0) { result[cluster] = previous[cluster]; continue; }
            var mean = new float[vectors[0].Length];
            foreach (var vector in vectors)
                for (var index = 0; index < mean.Length; index++) mean[index] += vector[index];
            var norm = Math.Sqrt(mean.Sum(value => value * value));
            if (norm > 0) for (var index = 0; index < mean.Length; index++) mean[index] /= (float)norm;
            result[cluster] = mean;
        }
        return result;
    }

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count != right.Count) return -1;
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }
        return leftNorm == 0 || rightNorm == 0 ? -1 : dot / Math.Sqrt(leftNorm * rightNorm);
    }
}
