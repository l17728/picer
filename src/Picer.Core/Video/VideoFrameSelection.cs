namespace Picer.Core.Video;

public static class VideoFrameSelection
{
    public static async Task<IReadOnlyList<T>> MaterializeAsync<T>(IReadOnlyList<T> frames, int maximumUsableFrames,
        Func<T, int, Task<T?>> load, CancellationToken token) where T : class
    {
        if (maximumUsableFrames < 1) throw new ArgumentOutOfRangeException(nameof(maximumUsableFrames));
        if (maximumUsableFrames >= frames.Count)
        {
            using var slots = new SemaphoreSlim(4);
            var all = await Task.WhenAll(frames.Select(async (frame, ordinal) =>
            {
                await slots.WaitAsync(token);
                try { return await load(frame, ordinal); }
                finally { slots.Release(); }
            }));
            return all.OfType<T>().ToArray();
        }
        var result = new List<T>();
        for (var offset = 0; offset < frames.Count && result.Count < maximumUsableFrames;)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(Math.Min(4, frames.Count - offset), maximumUsableFrames - result.Count);
            var start = offset;
            var batch = await Task.WhenAll(Enumerable.Range(start, count).Select(ordinal => load(frames[ordinal], ordinal)));
            result.AddRange(batch.OfType<T>());
            offset += count;
        }
        return result;
    }
}
