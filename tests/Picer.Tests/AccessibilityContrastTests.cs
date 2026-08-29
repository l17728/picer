namespace Picer.Tests;

public sealed class AccessibilityContrastTests
{
    [Theory]
    [InlineData(0x59, 0x59, 0x59, 0xFF, 0xFF, 0xFF)]
    [InlineData(0xD4, 0xD4, 0xD4, 0x1E, 0x1E, 0x1E)]
    [InlineData(0x55, 0x55, 0x55, 0xF0, 0xF0, 0xF0)]
    public void SmallTextColorsMeetWcagAa(int fr, int fg, int fb, int br, int bg, int bb)
    {
        Assert.True(Contrast(fr, fg, fb, br, bg, bb) >= 4.5);
    }

    private static double Contrast(int fr, int fg, int fb, int br, int bg, int bb)
    {
        static double Channel(int value)
        {
            var c = value / 255d;
            return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
        }
        static double Luminance(int r, int g, int b) => .2126 * Channel(r) + .7152 * Channel(g) + .0722 * Channel(b);
        var first = Luminance(fr, fg, fb);
        var second = Luminance(br, bg, bb);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }
}
