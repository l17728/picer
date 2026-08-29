using Picer.Core.Diagnostics;

namespace Picer.Tests;

public sealed class VirtualizationMathTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1_500_000)]
    [InlineData(2_970_000)]
    public void HundredThousandItems_RealizesOnlyViewportAndOverscan(double offset)
    {
        var range = VirtualizationMath.VisibleRange(100_000, 1200, 800, offset, 170, 208);

        Assert.InRange(range.Count, 1, 42);
        Assert.InRange(range.FirstIndex, 0, 99_999);
        Assert.InRange(range.LastIndex, range.FirstIndex, 99_999);
    }
}
