namespace Picer.Core.Diagnostics;

public readonly record struct VirtualizedRange(int FirstIndex, int LastIndex)
{
    public int Count => LastIndex < FirstIndex ? 0 : LastIndex - FirstIndex + 1;
}

public static class VirtualizationMath
{
    public static VirtualizedRange VisibleRange(int itemCount, double viewportWidth, double viewportHeight,
        double verticalOffset, double itemWidth, double itemHeight)
    {
        if (itemCount <= 0) return new(0, -1);
        if (itemWidth <= 0 || itemHeight <= 0) throw new ArgumentOutOfRangeException(nameof(itemWidth));
        var columns = Math.Max(1, (int)Math.Floor(Math.Max(itemWidth, viewportWidth) / itemWidth));
        var firstRow = Math.Max(0, (int)Math.Floor(Math.Max(0, verticalOffset) / itemHeight));
        var visibleRows = Math.Max(1, (int)Math.Ceiling(viewportHeight / itemHeight) + 1);
        return new(Math.Min(itemCount, firstRow * columns), Math.Min(itemCount - 1, (firstRow + visibleRows) * columns - 1));
    }
}
